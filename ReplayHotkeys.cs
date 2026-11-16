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
                ReplayGameKeys.Refresh();   // 玩家在游戏设置里改键后立即生效
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

                if (ReplayKeys.Down(ReplayKeys.ClipMark)) { ReplayRecorder.OnClipKey(); _status = "片段打点"; }
                if (ReplayKeys.Down(ReplayKeys.ToggleRecord)) ToggleRecord();
                if (ReplayKeys.Down(ReplayKeys.RecordPanel)) ReplayRecordUi.Toggle();
                if (ReplayKeys.Down(ReplayKeys.Lightbox)) ReplayLightboxUi.Toggle();
                if (ReplayKeys.Down(ReplayKeys.Editor)) ReplayEditorUi.Toggle();
                if (ReplayKeys.Down(ReplayKeys.PlayPause)) { if (ReplayEditorUi.Visible && ReplayPump.IsActive) ReplayPump.TogglePause(); }
                if (ReplayKeys.Down(ReplayKeys.Speed)) CycleSpeed();

                bool back = ReplayKeys.Down(ReplayKeys.Back10);
                bool fwd = ReplayKeys.Down(ReplayKeys.Forward10);
                if (ReplayPump.IsActive && (back || fwd))
                {
                    long target = (long)ReplayPump.PositionMs + (fwd ? 10000 : -10000);
                    if (target < 0) target = 0;
                    ReplayPump.Seek((uint)target);
                }

                if (ReplayKeys.Down(ReplayKeys.ReloadArt))
                {
                    if (shift) ReplayDiag.DumpResourceNames();
                    else { ReplayUiAssets.Reload(); ReplayRecordUi.SetStatus("贴图已重载"); }
                }
                if (ReplayKeys.Down(ReplayKeys.DumpScene)) { ReplayDiag.DumpSceneUi(); _status = "场景对象诊断"; }
                if (ReplayKeys.Down(ReplayKeys.DumpResources)) { ReplayDiag.DumpResourceNames(); _status = "资源诊断"; }
                if (ReplayKeys.Down(ReplayKeys.DumpBiomes)) { ReplayDiag.DumpBiomeSprites(); _status = "生态区图诊断"; }
                if (ReplayKeys.Down(ReplayKeys.AnimDiag))
                {
                    if (shift) ReplayDiag.DumpSceneUi();
                    else { ReplayMode.DumpAnimState(); _status = "动画诊断"; }
                }
                if (ReplayKeys.Down(ReplayKeys.Spectator))
                {
                    ReplaySpectator.Next();
                    _status = ReplaySpectator.TargetSummary();
                }
                if (ReplayKeys.Down(ReplayKeys.Freecam)) { ReplaySpectator.ToggleFreecam(); _status = "自由相机"; }
                
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