using System;
using System.IO;
using UnityEngine;

namespace Together.Replay
{
    /// <summary>
    /// 自带的极简 UI / 热键，不依赖游戏菜单，方便先验证链路。
    /// F6 录 / 停   F8 录像列表   F9 暂停   F10 倍速
    /// </summary>
    internal sealed class ReplayHotkeys : MonoBehaviour
    {
        bool _showList;
        string[] _files = Array.Empty<string>();
        Vector2 _scroll;
        string _status = "";

        void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F6)) { ReplayRecorder.OnClipKey(); _status = "片段打点"; }
                if (Input.GetKeyDown(KeyCode.F5)) ToggleRecord();
                if (Input.GetKeyDown(KeyCode.F7)) ReplayRecordUi.Toggle();
                if (Input.GetKeyDown(KeyCode.F8)) ReplayLightboxUi.Toggle();
                if (Input.GetKeyDown(KeyCode.F9)) ReplayEditorUi.Toggle();
                if (Input.GetKeyDown(KeyCode.Space)) { if (ReplayEditorUi.Visible && ReplayPump.IsActive) ReplayPump.TogglePause(); }
                if (Input.GetKeyDown(KeyCode.F10)) CycleSpeed();
                if (ReplayPump.IsActive && (Input.GetKeyDown(KeyCode.F1) || Input.GetKeyDown(KeyCode.F2)))
                {
                    long target = (long)ReplayPump.PositionMs + (Input.GetKeyDown(KeyCode.F2) ? 10000 : -10000);
                    if (target < 0) target = 0;
                    ReplayPump.Seek((uint)target);
                }
                if (Input.GetKeyDown(KeyCode.F12))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ReplayDiag.DumpResourceNames();
                    else { ReplayUiAssets.Reload(); ReplayRecordUi.SetStatus("贴图已重载"); }
                }
                if (Input.GetKeyDown(KeyCode.Insert)) { ReplayDiag.DumpSceneUi(); _status = "场景对象诊断"; }
                if (Input.GetKeyDown(KeyCode.Delete)) { ReplayDiag.DumpResourceNames(); _status = "资源诊断"; }
                if (Input.GetKeyDown(KeyCode.End)) { ReplayDiag.DumpBiomeSprites(); _status = "生态区图诊断"; }
                if (Input.GetKeyDown(KeyCode.F11))
                {
                    if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ReplayDiag.DumpSceneUi();
                    else { ReplayMode.DumpAnimState(); _status = "动画诊断"; }
                }
                if (Input.GetKeyDown(KeyCode.F3))
                {
                    ReplaySpectator.Next();
                    _status = ReplaySpectator.TargetSummary();
                }
                if (Input.GetKeyDown(KeyCode.F4)) { ReplaySpectator.ToggleFreecam(); _status = "自由相机"; }
                
            }
            catch (Exception e) { _status = e.Message; }
        }

        void ToggleRecord()
        {
            if (ReplayRecorder.IsRecording)
            {
                ReplayRecorder.Stop();
                _status = "录制已停止";
            }
            else if (ReplayRecorder.Start())
            {
                _status = "录制中 -> " + ReplayRecorder.CurrentPath;
            }
            else
            {
                _status = "录制启动失败（看 BepInEx 日志）";
            }
        }

        void CycleSpeed()
        {
            if (!ReplayPump.IsActive) return;
            float s = ReplayPump.Speed;
            float next = s >= 8f ? 0.25f : s * 2f;
            ReplayPump.SetSpeed(next);
            _status = "速度 " + next + "x";
        }

        void Refresh()
        {
            try { _files = ReplayPaths.List(); Array.Sort(_files); Array.Reverse(_files); }
            catch (Exception e) { _status = e.Message; }
        }

        static string Fmt(uint ms) => (ms / 60000) + ":" + ((ms / 1000) % 60).ToString("00");

        void OnGUI()
        {
            ReplayGapProbe.Mark("onGUI");
            if (!ReplayPlugin.ShowOverlay) return;

            // 光箱打开时只画光箱：录制槽位/剪辑器会被透过面板看到，看成"残留的线"
            if (ReplayLightboxUi.Visible)
            {
                ReplayLightboxUi.Draw();
                return;
            }

            ReplayRecordUi.Draw();
            ReplayEditorUi.Draw();
        }
    }
}