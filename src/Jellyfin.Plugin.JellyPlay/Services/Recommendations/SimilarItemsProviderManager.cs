using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyPlay.Services.Recommendations;

/// <summary>
/// Registers the plugin's local scorer with Jellyfin 12's similar-items
/// pipeline when the host exposes one — emitted at runtime because the plugin
/// compiles against Jellyfin.Controller 10.11, where those interfaces do not
/// exist. Recipe ported from Moonfin's SimilarItemsProviderManager (the
/// AddParts-replaces-set pitfall and the stock-provider preservation are
/// load-bearing; see the inline comments). No-op on 10.11 hosts.
/// </summary>
public class SimilarItemsProviderManager : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SimilarItemsService _similarItemsService;
    private readonly ILogger<SimilarItemsProviderManager> _logger;

    private Type? _localProviderType;
    private MethodInfo? _stockSupports;
    private MethodInfo? _stockGetSimilarItemsAsync;
    private PropertyInfo? _queryUser;
    private PropertyInfo? _queryLimit;
    private PropertyInfo? _queryExcludeItemIds;
    private readonly List<object> _stockProviders = [];

    /// <summary>
    /// True once this host's similar-items pipeline actually took the plugin's
    /// provider (Jellyfin 12+; the registration is reflection-guarded and a
    /// no-op on 10.11). Surfaced through the capabilities payload so clients
    /// can tell "stock /Items/Similar returns our scored list" from "the
    /// plugin route is the only similar source" without probing host versions.
    /// </summary>
    public bool SimilarPipelineRegistered { get; private set; }

    public SimilarItemsProviderManager(
        IServiceProvider serviceProvider,
        SimilarItemsService similarItemsService,
        ILogger<SimilarItemsProviderManager> logger)
    {
        _serviceProvider = serviceProvider;
        _similarItemsService = similarItemsService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            RegisterSimilarItemsProvider();
        }
        catch (Exception ex)
        {
            // Never fail host startup over an enrichment.
            _logger.LogWarning(ex, "JellyPlay similar-items provider registration failed (host may be 10.11).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RegisterSimilarItemsProvider()
    {
        var simMgrInterfaceType = Type.GetType("MediaBrowser.Controller.Library.ISimilarItemsManager, MediaBrowser.Controller");
        if (simMgrInterfaceType == null)
        {
            _logger.LogDebug("ISimilarItemsManager absent — Jellyfin 10.11 host; similar-items provider registration skipped.");
            return;
        }

        var provBaseType = Type.GetType("MediaBrowser.Controller.Library.ISimilarItemsProvider, MediaBrowser.Controller");
        var localProvType = Type.GetType("MediaBrowser.Controller.Library.ILocalSimilarItemsProvider, MediaBrowser.Controller");
        if (localProvType == null || provBaseType == null)
        {
            _logger.LogWarning("Similarity provider types missing from MediaBrowser.Controller; registration skipped.");
            return;
        }

        var simMgr = _serviceProvider.GetService(simMgrInterfaceType);
        if (simMgr == null)
        {
            _logger.LogWarning("ISimilarItemsManager resolved null from IServiceProvider; registration skipped.");
            return;
        }

        var addPartsMethod = simMgrInterfaceType.GetMethod("AddParts");
        if (addPartsMethod == null)
        {
            _logger.LogWarning("ISimilarItemsManager.AddParts not found; registration skipped.");
            return;
        }

        // AddParts REPLACES the provider set — the stock providers must be read
        // back and re-passed, or registering JellyPlay would silently drop
        // Jellyfin's own recommendations for every client.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        if (simMgr.GetType().GetField("_similarItemsProviders", flags)?.GetValue(simMgr) is not Array existingArr)
        {
            _logger.LogWarning(
                "Could not read existing similar-items providers from {ManagerType}; JellyPlay provider NOT registered (registering would drop the stock providers).",
                simMgr.GetType().FullName);
            return;
        }

        var providerInstance = CreateDynamicProvider(localProvType, provBaseType);
        if (providerInstance == null)
        {
            return;
        }

        var allList = new List<object>();
        foreach (var existing in existingArr)
        {
            if (existing != null && !existing.GetType().FullName!.Contains("JellyPlay", StringComparison.Ordinal))
            {
                allList.Add(existing);
            }
        }

        allList.Insert(0, providerInstance);

        var arr = Array.CreateInstance(provBaseType, allList.Count);
        for (var i = 0; i < allList.Count; i++)
        {
            arr.SetValue(allList[i], i);
        }

        addPartsMethod.Invoke(simMgr, [arr]);
        SimilarPipelineRegistered = true;
        _logger.LogInformation("JellyPlay similar-items provider registered ahead of {Count} stock providers.", allList.Count - 1);
    }

    private object? CreateDynamicProvider(Type localProvType, Type provBaseType)
    {
        var ifaceSupports = localProvType.GetMethod("Supports")!;
        var ifaceGetSimilar = localProvType.GetMethod("GetSimilarItemsAsync")!;
        var queryType = ifaceGetSimilar.GetParameters()[1].ParameterType;

        var ifacePropType = provBaseType.GetProperty("Type")!;
        var metaPluginEnumType = ifacePropType.PropertyType;
        if (!Enum.TryParse(metaPluginEnumType, "LocalSimilarityProvider", out var localSimilarityKind) || localSimilarityKind == null)
        {
            _logger.LogWarning("{EnumType} has no LocalSimilarityProvider member; registration skipped.", metaPluginEnumType.FullName);
            return null;
        }

        var asmBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Jellyfin.Plugin.JellyPlay.DynamicSimilarity"), AssemblyBuilderAccess.Run);
        var typeBuilder = asmBuilder.DefineDynamicModule("MainModule").DefineType(
            "JellyPlaySimilarItemsProviderDynamic",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(object),
            [localProvType]);

        const MethodAttributes ifaceMethodAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.Final;
        const MethodAttributes propMethodAttrs = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.Final;

        var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var ilCtor = ctor.GetILGenerator();
        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        ilCtor.Emit(OpCodes.Ret);

        var propName = typeBuilder.DefineProperty("Name", PropertyAttributes.None, typeof(string), Type.EmptyTypes);
        var getName = typeBuilder.DefineMethod("get_Name", propMethodAttrs, typeof(string), Type.EmptyTypes);
        var ilName = getName.GetILGenerator();
        ilName.Emit(OpCodes.Ldstr, "JellyPlay");
        ilName.Emit(OpCodes.Ret);
        propName.SetGetMethod(getName);
        typeBuilder.DefineMethodOverride(getName, provBaseType.GetProperty("Name")!.GetGetMethod()!);

        var propType = typeBuilder.DefineProperty("Type", PropertyAttributes.None, metaPluginEnumType, Type.EmptyTypes);
        var getType = typeBuilder.DefineMethod("get_Type", propMethodAttrs, metaPluginEnumType, Type.EmptyTypes);
        var ilType = getType.GetILGenerator();
        ilType.Emit(OpCodes.Ldc_I4, Convert.ToInt32(localSimilarityKind, CultureInfo.InvariantCulture));
        ilType.Emit(OpCodes.Ret);
        propType.SetGetMethod(getType);
        typeBuilder.DefineMethodOverride(getType, ifacePropType.GetGetMethod()!);

        // Jellyfin caches only remote providers — never read for local ones; the interface still demands it.
        var ifacePropCache = provBaseType.GetProperty("CacheDuration")!;
        var nullableTimeSpanType = ifacePropCache.PropertyType;
        var propCache = typeBuilder.DefineProperty("CacheDuration", PropertyAttributes.None, nullableTimeSpanType, Type.EmptyTypes);
        var getCache = typeBuilder.DefineMethod("get_CacheDuration", propMethodAttrs, nullableTimeSpanType, Type.EmptyTypes);
        var ilCache = getCache.GetILGenerator();
        ilCache.Emit(OpCodes.Ldc_R8, 24.0);
        ilCache.Emit(OpCodes.Call, typeof(TimeSpan).GetMethod("FromHours", [typeof(double)])!);
        ilCache.Emit(OpCodes.Newobj, nullableTimeSpanType.GetConstructor([typeof(TimeSpan)])!);
        ilCache.Emit(OpCodes.Ret);
        propCache.SetGetMethod(getCache);
        typeBuilder.DefineMethodOverride(getCache, ifacePropCache.GetGetMethod()!);

        // The emitted methods hold no state — they forward through static delegate fields.
        var fSupports = typeBuilder.DefineField("_supportsDelegate", typeof(Func<Type, bool>), FieldAttributes.Public | FieldAttributes.Static);
        var fGetSimilar = typeBuilder.DefineField("_getSimilarDelegate", typeof(Func<BaseItem, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>), FieldAttributes.Public | FieldAttributes.Static);

        var mSupports = typeBuilder.DefineMethod("Supports", ifaceMethodAttrs, typeof(bool), ifaceSupports.GetParameters().Select(p => p.ParameterType).ToArray());
        var ilSupports = mSupports.GetILGenerator();
        ilSupports.Emit(OpCodes.Ldsfld, fSupports);
        ilSupports.Emit(OpCodes.Ldarg_1);
        ilSupports.Emit(OpCodes.Callvirt, typeof(Func<Type, bool>).GetMethod("Invoke")!);
        ilSupports.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(mSupports, ifaceSupports);

        var mGetSimilar = typeBuilder.DefineMethod(
            "GetSimilarItemsAsync",
            ifaceMethodAttrs,
            ifaceGetSimilar.ReturnType,
            ifaceGetSimilar.GetParameters().Select(p => p.ParameterType).ToArray());
        var ilGetSimilar = mGetSimilar.GetILGenerator();
        ilGetSimilar.Emit(OpCodes.Ldsfld, fGetSimilar);
        ilGetSimilar.Emit(OpCodes.Ldarg_1);
        ilGetSimilar.Emit(OpCodes.Ldarg_2);
        ilGetSimilar.Emit(OpCodes.Ldarg_3);
        ilGetSimilar.Emit(OpCodes.Callvirt, typeof(Func<BaseItem, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>).GetMethod("Invoke")!);
        ilGetSimilar.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(mGetSimilar, ifaceGetSimilar);

        var generatedType = typeBuilder.CreateType();
        if (generatedType == null)
        {
            _logger.LogWarning("Dynamic JellyPlay similar-items provider creation failed.");
            return null;
        }

        generatedType.GetField("_supportsDelegate")!.SetValue(null, (Func<Type, bool>)HandleSupports);
        generatedType.GetField("_getSimilarDelegate")!.SetValue(
            null,
            (Func<BaseItem, object, CancellationToken, Task<IReadOnlyList<BaseItem>>>)HandleGetSimilarAsync);

        _localProviderType = localProvType;
        _stockSupports = ifaceSupports;
        _stockGetSimilarItemsAsync = ifaceGetSimilar;
        _queryUser = queryType.GetProperty("User");
        _queryLimit = queryType.GetProperty("Limit");
        _queryExcludeItemIds = queryType.GetProperty("ExcludeItemIds");

        return Activator.CreateInstance(generatedType);
    }

    private bool HandleSupports(Type? itemType)
    {
        if (itemType == null)
        {
            return false;
        }

        return typeof(Movie).IsAssignableFrom(itemType) || typeof(Series).IsAssignableFrom(itemType);
    }

    private async Task<IReadOnlyList<BaseItem>> HandleGetSimilarAsync(
        BaseItem item,
        object? rawQuery,
        CancellationToken cancellationToken)
    {
        object? user = null;
        int? limit = null;
        IReadOnlyList<Guid>? excludeItemIds = null;
        if (rawQuery != null)
        {
            user = _queryUser?.GetValue(rawQuery);
            limit = _queryLimit?.GetValue(rawQuery) as int?;
            excludeItemIds = _queryExcludeItemIds?.GetValue(rawQuery) as IReadOnlyList<Guid>;
        }

        try
        {
            return await _similarItemsService.GetSimilarItemsAsync(item, user, limit, excludeItemIds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Empty lets the stock providers fill the slots instead of failing the request.
            _logger.LogWarning(ex, "JellyPlay similar-items scoring failed for '{ItemName}'.", item.Name);
            return Array.Empty<BaseItem>();
        }
    }
}
