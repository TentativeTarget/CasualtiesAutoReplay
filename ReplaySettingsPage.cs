using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Together.Replay
{
    /// <summary>
    /// 把 rec 设置插到「多人游戏」页的"反馈/报告"之前。
    /// 做法：包装 mod 的 bugreportFunctionCreator —— 它在构建页面走到反馈段时才调用，
    /// 我们借这个时机先插入三行，再执行原来的反馈逻辑。
    /// </summary>
    internal static class ReplaySettingsPage
    {
        static bool _wrapped;

        public static void WrapFeedbackDelegate()
        {
            if (_wrapped) return;
            try
            {
                var t = AccessTools.TypeByName("Together.SettingsMenu_Start_MultiplayerPatch");
                var f = t?.GetField("bugreportFunctionCreator", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f == null) return;

                var original = f.GetValue(null);
                if (original == null) return;   // 由 mod 之后设置，下次再包

                var delType = original.GetType();
                var mi = typeof(ReplaySettingsPage).GetMethod(nameof(OnBeforeFeedback), BindingFlags.Static | BindingFlags.NonPublic);

                // 先执行我们的插入，再执行原来的反馈逻辑（链式，不破坏原功能）
                var ours = Delegate.CreateDelegate(delType, mi);
                var combined = Delegate.Combine(ours, (Delegate)original);
                f.SetValue(null, combined);
                _wrapped = true;
                ReplayLog.Info("ui: 已把 rec 设置插到反馈栏之前");
            }
            catch (Exception e) { ReplayLog.Warn("ui: 包装反馈委托失败 " + e.Message); }
        }

        static void OnBeforeFeedback()
        {
            AddRows();
        }

        static void AddRows()
        {
            try
            {
                var content = SettingsMenu_Start_MultiplayerPatch.content;
                if (content == null) return;

                // 单独一栏：标题样式与聊天的"聊天"标题一致（青色、加大）
                AddSectionHeader(content, "录制");

                AddFloatRow(content, "录制按钮位置 X", "0 = 最左，1 = 最右", 0f, 1f, ReplayPlugin.SlotPosX, v =>
                {
                    ReplayPlugin.SlotPosX = v;
                    if (ReplayPlugin.SlotPosXCfg != null) ReplayPlugin.SlotPosXCfg.Value = v;
                });
                AddFloatRow(content, "录制按钮位置 Y", "0 = 最下，1 = 最上", 0f, 1f, ReplayPlugin.SlotPosY, v =>
                {
                    ReplayPlugin.SlotPosY = v;
                    if (ReplayPlugin.SlotPosYCfg != null) ReplayPlugin.SlotPosYCfg.Value = v;
                });
                AddFloatRow(content, "录制按钮尺寸", "按钮大小倍率", 0.4f, 3f, ReplayPlugin.SlotScale, v =>
                {
                    ReplayPlugin.SlotScale = v;
                    if (ReplayPlugin.SlotScaleCfg != null) ReplayPlugin.SlotScaleCfg.Value = v;
                });

                try { ReplayPlugin.Instance?.Config?.Save(); } catch { }
                ReplayLog.Info("ui: rec 三行设置已插入（反馈栏之前）");
            }
            catch (Exception e) { ReplayLog.Warn("ui: 插入设置失败 " + e); }
        }

        static void AddSectionHeader(RectTransform content, string title)
        {
            try
            {
                GameObject g;
                UnityEngine.UI.Button button;
                TMPro.TextMeshProUGUI text;
                SettingsMenu_Start_MultiplayerPatch.CreateSettingButton("     " + title,
                    ref SettingsMenu_Start_MultiplayerPatch.contentHeight, out g, out button, out text);

                if (text != null)
                {
                    text.fontSize += 10;
                    text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                    text.color = Color.cyan;
                }
                if (button != null)
                {
                    var img = button.GetComponent<UnityEngine.UI.Image>();
                    if (img != null) img.color = Color.cyan;
                    UnityEngine.Object.Destroy(button);   // 只做标题，不要点击
                }
                if (g != null) g.name = "replay_section_" + title;
            }
            catch (Exception e) { ReplayLog.Warn("ui: 加标题失败 " + e.Message); }
        }

        static void AddFloatRow(RectTransform content, string name, string desc,
                                float min, float max, float value, Action<float> onChange)
        {
            GameObject g = null;
            try { g = Utils.Create("Special/GameSettingFloat", content); }
            catch (Exception e) { ReplayLog.Warn("ui: 创建设置行失败 " + e.Message); return; }
            if (g == null) return;

            var slider = g.transform.GetChild(1).GetComponent<Slider>();
            if (slider != null)
            {
                slider.minValue = min;
                slider.maxValue = max;
                slider.SetValueWithoutNotify(value);
                var label = g.transform.GetChild(2).GetComponent<TextMeshProUGUI>();
                if (label != null) label.text = value.ToString("0.00");
                slider.onValueChanged.AddListener(v =>
                {
                    onChange?.Invoke(v);
                    if (label != null) label.text = v.ToString("0.00");
                });
            }

            SettingsMenu_Start_MultiplayerPatch.SetupSetting(g, name, desc,
                ref SettingsMenu_Start_MultiplayerPatch.contentHeight);
            g.name = "replay_setting_" + name;
        }
    }
}