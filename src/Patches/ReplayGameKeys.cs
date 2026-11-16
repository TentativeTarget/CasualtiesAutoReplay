using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 把我们的热键注册进游戏自带的按键设置（设置 → 输入页），
    /// 这样玩家在游戏里就能改键，不用改配置文件。
    ///
    /// 做法：在 `Settings.DefaultSettings()` 返回的列表里追加 `SettingKeybind` 实例。
    /// 游戏会把列表里的条目按 `category` 分页显示、按 `name` 存盘读盘，所以不需要碰 UI。
    /// 全部走反射，避免插件编译期依赖游戏 private 成员。
    /// </summary>
    internal static class ReplayGameKeys
    {
        public struct Slot
        {
            public string Name;
            public KeyCode Default;
            public object Setting;      // SettingKeybind 实例
            public FieldInfo ValueField;
            public Action<KeyCode> Set;
        }

        static readonly List<Slot> _slots = new List<Slot>();
        static Type _settingType, _keybindType, _categoryType;
        static FieldInfo _nameField, _valueField, _categoryField, _applyField;
        static bool _ready;

        /// <summary>要在游戏里注册的条目（名字 + 默认键 + 回写到 ReplayKeys 的字段）。</summary>
        public static void Declare(string name, KeyCode def, Action<KeyCode> set)
        {
            _slots.Add(new Slot { Name = name, Default = def, Set = set });
        }

        public static bool HasEntries => _slots.Count > 0;

        static bool Resolve()
        {
            if (_ready) return true;
            try
            {
                _keybindType = AccessTools.TypeByName("SettingKeybind");
                _settingType = AccessTools.TypeByName("Setting");
                _categoryType = AccessTools.TypeByName("SettingCategory");
                if (_keybindType == null || _settingType == null || _categoryType == null) return false;

                _nameField = Field(_settingType, "name") ?? Field(_keybindType, "name");
                _applyField = Field(_settingType, "apply") ?? Field(_keybindType, "apply");
                _categoryField = Field(_settingType, "category") ?? Field(_keybindType, "category");
                _valueField = Field(_keybindType, "value") ?? Field(_settingType, "value");
                _ready = _nameField != null && _valueField != null && _categoryField != null;
                if (!_ready) ReplayLog.Warn("gamekeys: Setting 字段没找全，退回配置文件键位");
                return _ready;
            }
            catch (Exception e)
            {
                ReplayLog.Warn("gamekeys: 反射失败 " + e.Message);
                return false;
            }
        }

        static FieldInfo Field(Type t, string name)
        {
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var f = cur.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>把条目补进游戏的设置列表（DefaultSettings 的 Postfix 调用）。</summary>
        public static void Attach(IList settings)
        {
            if (settings == null || !Resolve()) return;

            object inputCategory = null;
            try { inputCategory = Enum.Parse(_categoryType, "Input"); } catch { }

            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                try
                {
                    if (FindExisting(settings, slot.Name) != null) { slot = _slots[i]; continue; }

                    var inst = Activator.CreateInstance(_keybindType, true);
                    _nameField.SetValue(inst, slot.Name);
                    if (inputCategory != null && _categoryField != null) _categoryField.SetValue(inst, inputCategory);
                    if (_applyField != null) _applyField.SetValue(inst, null);
                    _valueField.SetValue(inst, slot.Default);
                    settings.Add(inst);

                    slot.Setting = inst;
                    slot.ValueField = _valueField;
                    _slots[i] = slot;
                }
                catch (Exception e)
                {
                    ReplayLog.Warn("gamekeys: 注册 " + slot.Name + " 失败 - " + e.Message);
                }
            }

            ReplayLog.Info("gamekeys: 已注册 " + _slots.Count + " 个按键到游戏设置（设置 → 输入）");
        }

        static object FindExisting(IList settings, string name)
        {
            for (int i = 0; i < settings.Count; i++)
            {
                var s = settings[i];
                if (s == null) continue;
                try
                {
                    if (_nameField.GetValue(s) as string == name) return s;
                }
                catch { }
            }
            return null;
        }

        /// <summary>每帧把游戏里的当前键位读回 ReplayKeys（玩家随时改，立即生效）。</summary>
        public static void Refresh()
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (slot.Setting == null || slot.ValueField == null) continue;
                try
                {
                    var v = slot.ValueField.GetValue(slot.Setting);
                    if (v is KeyCode kc) slot.Set(kc);
                }
                catch { }
            }
        }

        /// <summary>注册完把当前值同步一次（列表可能来自存档）。</summary>
        public static void BindInstances(IList settings)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (slot.Setting != null) continue;
                var found = FindExisting(settings, slot.Name);
                if (found == null) continue;
                slot.Setting = found;
                slot.ValueField = _valueField;
                _slots[i] = slot;
            }
        }
    }

    /// <summary>在游戏的默认设置列表末尾追加我们的按键条目。</summary>
    [HarmonyPatch]
    internal static class GameKeybindSettingsPatch
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Settings");
            return t == null ? null : AccessTools.Method(t, "DefaultSettings");
        }

        static bool Prepare() => TargetMethod() != null && ReplayPlugin.UseGameKeybinds;

        static void Postfix(object __result)
        {
            try
            {
                var list = __result as IList;
                if (list == null) return;
                ReplayGameKeys.Attach(list);
                ReplayGameKeys.BindInstances(list);
            }
            catch (Exception e) { ReplayLog.Warn("gamekeys: 追加失败 " + e.Message); }
        }
    }
}
