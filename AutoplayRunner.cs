csharp name=AutoplayRunner.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using UnityEngine;
using UnityModManagerNet;

namespace AutoplayRunner
{
    [Serializable]
    public class AutoplayFile
    {
        public string mapFilename;
        public float offset;
        public int count;
        public float defaultBpm;
        public SetSpeedEvent[] setSpeedEvents;
        public TimingEntry[] timings;
    }

    [Serializable]
    public class SetSpeedEvent
    {
        public int floor;
        public float bpm;
    }

    [Serializable]
    public class TimingEntry
    {
        public int floor;
        public double time;
    }

    public static class Main
    {
        public static UnityModManager.ModEntry modEntry;

        static AutoplayFile autoplay;
        static List<(int floor, double triggerMs)> schedule;
        static CancellationTokenSource cts;
        static Stopwatch sw;

        static string loadedFilePath = "";
        static bool isPlaying = false;
        static bool simulateMouse = true;
        static ushort vkKey = 0x20; // Space
        static bool requireGameFocus = true;

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public INPUTUNION U; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint KEYEVENTF_KEYDOWN = 0x0000;
        const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static bool Load(UnityModManager.ModEntry entry)
        {
            modEntry = entry;
            modEntry.OnGUI = OnGUI;
            modEntry.OnToggle = OnToggle;
            modEntry.OnSaveGUI = OnSaveGUI;
            loadedFilePath = Path.Combine(modEntry.Path, "autoplay.json");
            modEntry.Logger.Log("[AutoplayRunner] loaded");
            return true;
        }

        static bool OnToggle(UnityModManager.ModEntry entry, bool value) => true;
        static void OnSaveGUI(UnityModManager.ModEntry entry) { /* persist if desired */ }

        static void OnGUI(UnityModManager.ModEntry entry)
        {
            GUILayout.BeginVertical("Box");
            GUILayout.Label("Autoplay Runner (UMM)");
            GUILayout.Space(6);

            GUILayout.Label($"모드 경로: {modEntry.Path}");
            GUILayout.Label($"로드된 파일: {(string.IsNullOrEmpty(loadedFilePath) ? "(없음)" : loadedFilePath)}");

            if (GUILayout.Button("모드폴더의 autoplay.json 로드"))
            {
                string defaultPath = Path.Combine(modEntry.Path, "autoplay.json");
                if (File.Exists(defaultPath))
                {
                    loadedFilePath = defaultPath;
                    TryLoadAutoplay(loadedFilePath);
                }
                else modEntry.Logger.Log("모드 폴더에 autoplay.json 없음");
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("직접 경로:", GUILayout.Width(110));
            loadedFilePath = GUILayout.TextField(loadedFilePath, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("로드", GUILayout.Width(80)))
            {
                if (File.Exists(loadedFilePath)) TryLoadAutoplay(loadedFilePath);
                else modEntry.Logger.Log($"파일 없음: {loadedFilePath}");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (autoplay != null)
            {
                GUILayout.Label($"맵: {autoplay.mapFilename}  타일수: {autoplay.count}  offset: {autoplay.offset} ms");
                GUILayout.Label($"스케줄 항목: {(schedule != null ? schedule.Count.ToString() : "0")}");

                GUILayout.Space(4);
                simulateMouse = GUILayout.Toggle(simulateMouse, "마우스 좌클릭 시뮬레이션 (체크해제 시 키)");
                if (!simulateMouse)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("VK hex:", GUILayout.Width(120));
                    string hex = GUILayout.TextField(vkKey.ToString("X"), GUILayout.Width(80));
                    if (GUILayout.Button("설정", GUILayout.Width(60)))
                    {
                        try { vkKey = Convert.ToUInt16(hex, 16); }
                        catch { modEntry.Logger.Log("VK hex 변환 실패"); }
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Label($"현재 VK: 0x{vkKey:X}");
                }
                requireGameFocus = GUILayout.Toggle(requireGameFocus, "게임 포커스 필요");

                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                if (!isPlaying)
                {
                    if (GUILayout.Button("재생 시작", GUILayout.Height(40)))
                    {
                        if (autoplay != null && schedule != null && schedule.Count > 0) StartAutoplay();
                        else modEntry.Logger.Log("스케줄 없음");
                    }
                }
                else
                {
                    if (GUILayout.Button("정지", GUILayout.Height(40))) StopAutoplay();
                }
                if (GUILayout.Button("스케줄 로그", GUILayout.Height(40)))
                {
                    if (schedule != null) foreach (var s in schedule) modEntry.Logger.Log($"tile {s.floor} at {s.triggerMs} ms");
                }
                GUILayout.EndHorizontal();
            }
            else GUILayout.Label("autoplay.json 을 로드하세요.");
            GUILayout.EndVertical();
        }

        static void TryLoadAutoplay(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                autoplay = JsonUtility.FromJson<AutoplayFile>(json);
                if (autoplay == null || autoplay.timings == null || autoplay.timings.Length == 0)
                {
                    modEntry.Logger.Log("파싱 실패 또는 timings empty");
                    autoplay = null;
                    return;
                }
                schedule = new List<(int floor, double triggerMs)>();
                double offsetMs = autoplay.offset;
                foreach (var t in autoplay.timings)
                {
                    double trigger = t.time - offsetMs;
                    schedule.Add((t.floor, trigger));
                }
                schedule.Sort((a, b) => a.triggerMs.CompareTo(b.triggerMs));
                modEntry.Logger.Log($"autoplay.json 로드됨: {path}  항목: {schedule.Count}");
            }
            catch (Exception ex) { modEntry.Logger.Log($"로드 실패: {ex}"); }
        }

        static void StartAutoplay()
        {
            if (isPlaying || schedule == null || schedule.Count == 0) return;
            cts = new CancellationTokenSource();
            sw = new Stopwatch();
            sw.Start();
            isPlaying = true;
            modEntry.Logger.Log("Autoplay 시작 (song-start 시점에 버튼을 누르셨다고 가정)");

            Task.Run(async () =>
            {
                try
                {
                    int idx = 0;
                    while (idx < schedule.Count && schedule[idx].triggerMs < 0) idx++;
                    for (int i = idx; i < schedule.Count; i++)
                    {
                        var ev = schedule[i];
                        double targetMs = ev.triggerMs;
                        while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < targetMs)
                        {
                            long remaining = (long)(targetMs - sw.ElapsedMilliseconds);
                            if (remaining > 20) await Task.Delay(15);
                            else await Task.Delay(1);
                        }
                        if (cts.IsCancellationRequested) break;
                        if (requireGameFocus && !IsGameWindowFocused())
                        {
                            modEntry.Logger.Log($"게임 비포커스: 타일 {ev.floor} (포커스 필요)");
                        }
                        SimulateHit();
                        await Task.Delay(1);
                    }
                }
                catch (Exception ex) { modEntry.Logger.Log($"루프 오류: {ex}"); }
                finally
                {
                    isPlaying = false;
                    sw.Stop();
                    modEntry.Logger.Log("Autoplay 종료");
                }
            }, cts.Token);
        }

        static void StopAutoplay()
        {
            if (!isPlaying) return;
            try { cts.Cancel(); } catch { }
            isPlaying = false;
            modEntry.Logger.Log("Autoplay 중단됨");
        }

        static void SimulateHit()
        {
            if (simulateMouse) SimulateMouseLeftClick();
            else SimulateKeyPress(vkKey);
        }

        static void SimulateMouseLeftClick()
        {
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].U.mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = MOUSEEVENTF_LEFTDOWN, time = 0, dwExtraInfo = UIntPtr.Zero };
            inputs[1].type = INPUT_MOUSE;
            inputs[1].U.mi = new MOUSEINPUT { dx = 0, dy = 0, mouseData = 0, dwFlags = MOUSEEVENTF_LEFTUP, time = 0, dwExtraInfo = UIntPtr.Zero };
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length) modEntry.Logger.Log($"SendInput 실패: {Marshal.GetLastWin32Error()} (sent {sent})");
        }

        static void SimulateKeyPress(ushort vk)
        {
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYDOWN, time = 0, dwExtraInfo = UIntPtr.Zero };
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = UIntPtr.Zero };
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length) modEntry.Logger.Log($"SendInput 실패: {Marshal.GetLastWin32Error()} (sent {sent})");
        }

        static bool IsGameWindowFocused()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(fg, out pid);
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch { return true; }
        }
    }
}
