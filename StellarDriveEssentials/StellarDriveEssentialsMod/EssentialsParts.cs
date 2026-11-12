using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using UI.BuildMenu;
using UnityEngine;
using Object = UnityEngine.Object;
using BuildMenuRow = UI.BuildMenu.BuildMenuTabConfig.BuildMenuRow;

namespace StellarDriveEssentials;

internal sealed class EssentialsPartDefinition
{
    internal ushort Id;
    internal string Name;
    internal string Donor;
    internal int BuildRow;
    internal Action<PartSettings, GameObject> Configure;
}

// Only the six Essentials parts are registered here. Native state classes and save IDs stay unchanged.
internal static class EssentialsParts
{
    private static readonly List<EssentialsPartDefinition> Definitions = new List<EssentialsPartDefinition>();
    private static readonly Dictionary<ushort, PartSettings> Parts = new Dictionary<ushort, PartSettings>();
    private sealed class MenuBinding { internal BuildMenuTabConfig Tab; }
    private static ConditionalWeakTable<BuildMenu, MenuBinding> Menus = new ConditionalWeakTable<BuildMenu, MenuBinding>();
    private static readonly WeakObjectSet<BuildMenu> LiveMenus = new WeakObjectSet<BuildMenu>();
    private static readonly WeakObjectSet<PartSettingsProvider> Providers = new WeakObjectSet<PartSettingsProvider>();
    private static readonly HashSet<ushort> MissingReported = new HashSet<ushort>();
    private static FieldInfo _partsField, _tabsField;
    private static GameObject _holder;
    private static bool _building, _notifying, _installed;
    private static Action<string> _log;
    internal static Action PartsReady;

    internal static void Install(HarmonyLib.Harmony harmony, Action<string> log)
    {
        if (_installed) return;
        _partsField = AccessTools.Field(typeof(PartSettingsProvider), "_partsDict");
        _tabsField = AccessTools.Field(typeof(BuildMenu), "tabsConfig");
        MethodInfo settings = AccessTools.DeclaredMethod(typeof(PartSettingsProvider), "EnsureDictIsCached", Type.EmptyTypes);
        MethodInfo menu = AccessTools.DeclaredMethod(typeof(BuildMenu), "Start", Type.EmptyTypes);
        if (_partsField?.FieldType != typeof(Dictionary<ushort, PartSettings>)
            || _tabsField?.FieldType != typeof(BuildMenuTabConfig[]) || settings == null || menu == null)
            throw new MissingMemberException("The native part registry/build menu changed; Essentials registration was not installed.");
        _log = log;
        harmony.Patch(settings, prefix: new HarmonyMethod(typeof(EssentialsParts), nameof(BeforeSettingsCache)),
            postfix: new HarmonyMethod(typeof(EssentialsParts), nameof(AfterSettingsCache)));
        harmony.Patch(menu, prefix: new HarmonyMethod(typeof(EssentialsParts), nameof(BeforeBuildMenu)) { priority = Priority.Last });
        _installed = true;
    }

    internal static void Register(EssentialsPartDefinition definition)
    {
        if (definition == null || definition.Id < 8901 || definition.Id > 8906
            || string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Donor)
            || definition.BuildRow < 0 || definition.Configure == null)
            throw new ArgumentException("Invalid Essentials part definition.", nameof(definition));
        if (Definitions.Any(d => d.Id == definition.Id || d.Name == definition.Name))
            throw new InvalidOperationException("Duplicate Essentials part ID/name: " + definition.Id);
        Definitions.Add(definition);
    }

    internal static PartSettings Get(ushort id)
    {
        EnsureAll();
        Parts.TryGetValue(id, out PartSettings settings);
        return settings;
    }

    private static void EnsureAll()
    {
        if (_building || Definitions.Count == 0 || Parts.Count == Definitions.Count) return;
        bool changed = false;
        _building = true;
        try
        {
            PartSettings[] loaded = Resources.FindObjectsOfTypeAll<PartSettings>();
            // Check all IDs before constructing anything: a collision must never silently replace another mod.
            foreach (EssentialsPartDefinition definition in Definitions)
                if (!Parts.ContainsKey(definition.Id) && loaded.Any(p => p && p.id == definition.Id))
                    throw new InvalidOperationException("Essentials part ID " + definition.Id + " is already in use.");
            foreach (EssentialsPartDefinition definition in Definitions)
            {
                if (Parts.ContainsKey(definition.Id)) continue;
                PartSettings donor = loaded.FirstOrDefault(p => p && p.name == definition.Donor && p.part);
                if (!donor)
                {
                    if (MissingReported.Add(definition.Id))
                        _log?.Invoke("Essentials is waiting for donor " + definition.Donor + " (part " + definition.Id + ").");
                    continue;
                }
                Parts.Add(definition.Id, CreatePart(definition, donor));
                MissingReported.Remove(definition.Id);
                changed = true;
            }
        }
        finally { _building = false; }
        if (changed && !_notifying)
        {
            _notifying = true;
            try { PartsReady?.Invoke(); }
            finally { _notifying = false; }
        }
    }

    private static PartSettings CreatePart(EssentialsPartDefinition definition, PartSettings donor)
    {
        if (!_holder)
        {
            _holder = new GameObject("Essentials_PartTemplates") { hideFlags = HideFlags.HideAndDontSave };
            // PartSettings.GetSockets uses GetComponentsInChildren without includeInactive.
            // Keep templates active, as before, but outside the playable world.
            _holder.transform.position = new Vector3(0f, -100000f, 0f);
            Object.DontDestroyOnLoad(_holder);
        }
        PartSettings settings = null;
        GameObject prefab = null;
        try
        {
            prefab = Object.Instantiate(donor.part, _holder.transform, false);
            prefab.name = definition.Name;
            prefab.hideFlags = HideFlags.HideAndDontSave;
            prefab.transform.localPosition = Vector3.zero;
            prefab.transform.localRotation = Quaternion.identity;
            settings = Object.Instantiate(donor);
            settings.name = definition.Name;
            settings.hideFlags = HideFlags.DontSave;
            settings.id = definition.Id;
            settings.part = prefab;
            definition.Configure(settings, prefab);
            // A callback must not accidentally mutate identity or leave a native donor alias in the save registry.
            if (settings.id != definition.Id || settings.name != definition.Name || !ReferenceEquals(settings.part, prefab))
                throw new InvalidOperationException("Essentials part identity changed during configuration: " + definition.Id);
            return settings;
        }
        catch
        {
            if (settings) Object.DestroyImmediate(settings);
            if (prefab) Object.DestroyImmediate(prefab);
            throw;
        }
    }

    private static void BeforeSettingsCache() => EnsureAll();

    private static void AfterSettingsCache(PartSettingsProvider __instance)
    {
        var current = (Dictionary<ushort, PartSettings>)_partsField.GetValue(__instance);
        if (current == null || Parts.Count == 0) return;
        Providers.Add(__instance);
        Dictionary<ushort, PartSettings> merged = null;
        foreach (var part in Parts)
        {
            if (current.TryGetValue(part.Key, out PartSettings existing))
            {
                if (!ReferenceEquals(existing, part.Value))
                    throw new InvalidOperationException("A different part replaced Essentials ID " + part.Key);
                continue;
            }
            if (merged == null) merged = new Dictionary<ushort, PartSettings>(current);
            merged.Add(part.Key, part.Value);
        }
        if (merged != null) _partsField.SetValue(__instance, merged);
    }

    private static void BeforeBuildMenu(BuildMenu __instance)
    {
        EnsureAll();
        var tabs = (BuildMenuTabConfig[])_tabsField.GetValue(__instance);
        if (tabs == null) return;
        Menus.TryGetValue(__instance, out MenuBinding binding);
        BuildMenuTabConfig previous = binding?.Tab;
        var others = tabs.Where(t => !ReferenceEquals(t, previous)).ToList();
        var rows = Definitions.OrderBy(d => d.BuildRow).ThenBy(d => d.Id)
            .Where(d => Parts.ContainsKey(d.Id)).GroupBy(d => d.BuildRow)
            .Select(g => new BuildMenuRow { parts = g.Select(d => Parts[d.Id]).ToArray() }).ToArray();
        if (rows.Length == 0) return;
        var ours = new BuildMenuTabConfig
        {
            name = Instruments.BuildTab,
            icon = others.Select(t => t?.icon).FirstOrDefault(icon => icon),
            rows = rows
        };
        others.Add(ours);
        Menus.Remove(__instance);
        Menus.Add(__instance, new MenuBinding { Tab = ours });
        LiveMenus.Add(__instance);
        _tabsField.SetValue(__instance, others.ToArray());
    }

    internal static void Release()
    {
        foreach (BuildMenu menu in LiveMenus.Live)
            if (menu && Menus.TryGetValue(menu, out MenuBinding binding) && _tabsField.GetValue(menu) is BuildMenuTabConfig[] tabs)
                _tabsField.SetValue(menu, tabs.Where(t => !ReferenceEquals(t, binding.Tab)).ToArray());
        Menus = new ConditionalWeakTable<BuildMenu, MenuBinding>();
        LiveMenus.Clear();
        foreach (PartSettingsProvider provider in Providers.Live)
            if (_partsField.GetValue(provider) is Dictionary<ushort, PartSettings> settings)
                foreach (var part in Parts)
                    if (settings.TryGetValue(part.Key, out PartSettings existing) && ReferenceEquals(existing, part.Value)) settings.Remove(part.Key);
        Providers.Clear();
        foreach (PartSettings settings in Parts.Values)
            if (settings) Object.Destroy(settings);
        Parts.Clear();
        Definitions.Clear();
        PartsReady = null;
        MissingReported.Clear();
        if (_holder) Object.Destroy(_holder);
        _holder = null;
        _log = null;
    }
}

// Keep shutdown cleanup without rooting every provider/menu created across world reloads.
internal sealed class WeakObjectSet<T> where T : class
{
    private ConditionalWeakTable<T, object> _seen = new ConditionalWeakTable<T, object>();
    private readonly List<WeakReference<T>> _references = new List<WeakReference<T>>();
    public void Add(T target)
    {
        if (_seen.TryGetValue(target, out _)) return;
        _seen.Add(target, new object());
        _references.RemoveAll(reference => !reference.TryGetTarget(out _));
        _references.Add(new WeakReference<T>(target));
    }
    public IEnumerable<T> Live
    {
        get
        {
            foreach (WeakReference<T> reference in _references)
                if (reference.TryGetTarget(out T target)) yield return target;
        }
    }
    public void Clear()
    {
        _seen = new ConditionalWeakTable<T, object>();
        _references.Clear();
    }
}
