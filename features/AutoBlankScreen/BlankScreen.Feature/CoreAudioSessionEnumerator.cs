using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UltimateToolbox.AutoBlankScreen.Feature;

    internal readonly struct CoreAudioSessionSnapshot
    {
        public CoreAudioSessionSnapshot(int processId, string? displayName, float peakLevel)
        {
            ProcessId = processId;
            DisplayName = displayName;
            PeakLevel = peakLevel;
        }

        public int ProcessId { get; }
        public string? DisplayName { get; }
        public float PeakLevel { get; }
    }

    /// <summary>
    /// Enumerates the current render-session meters directly through Core Audio.
    /// Every COM object obtained during an enumeration is released before the
    /// next polling interval, so session creation cannot accumulate wrappers.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class CoreAudioSessionEnumerator
    {
        private const uint DeviceStateActive = 0x00000001;
        private const uint ClsCtxAll = 0x00000017;
        private static readonly Guid AudioSessionManager2Id = typeof(IAudioSessionManager2).GUID;

        public static IReadOnlyList<CoreAudioSessionSnapshot> EnumerateRenderSessions()
        {
            var snapshots = new List<CoreAudioSessionSnapshot>();
            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? devices = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (!Succeeded(enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out devices))
                    || devices == null
                    || !Succeeded(devices.GetCount(out var deviceCount)))
                {
                    return snapshots;
                }

                for (var deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
                {
                    AppendDeviceSessions(devices, deviceIndex, snapshots);
                }
            }
            catch
            {
                // Audio endpoints can disappear while Windows is rebuilding the
                // audio graph. Return whatever was safely read this interval.
            }
            finally
            {
                ReleaseComObject(devices);
                ReleaseComObject(enumerator);
            }

            return snapshots;
        }

        private static void AppendDeviceSessions(
            IMMDeviceCollection devices,
            int deviceIndex,
            List<CoreAudioSessionSnapshot> snapshots)
        {
            IMMDevice? device = null;
            object? activatedManager = null;
            IAudioSessionManager2? sessionManager = null;
            IAudioSessionEnumerator? sessionEnumerator = null;

            try
            {
                if (!Succeeded(devices.Item(deviceIndex, out device)) || device == null)
                {
                    return;
                }

                var managerId = AudioSessionManager2Id;
                if (!Succeeded(device.Activate(ref managerId, ClsCtxAll, IntPtr.Zero, out activatedManager))
                    || activatedManager is not IAudioSessionManager2 manager)
                {
                    return;
                }

                sessionManager = manager;
                if (!Succeeded(sessionManager.GetSessionEnumerator(out sessionEnumerator))
                    || sessionEnumerator == null
                    || !Succeeded(sessionEnumerator.GetCount(out var sessionCount)))
                {
                    return;
                }

                for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
                {
                    AppendSession(sessionEnumerator, sessionIndex, snapshots);
                }
            }
            catch
            {
                // A single endpoint should not suppress activity from the rest.
            }
            finally
            {
                ReleaseComObject(sessionEnumerator);
                ReleaseComObject(activatedManager);
                ReleaseComObject(device);
            }
        }

        private static void AppendSession(
            IAudioSessionEnumerator sessionEnumerator,
            int sessionIndex,
            List<CoreAudioSessionSnapshot> snapshots)
        {
            IAudioSessionControl? session = null;
            try
            {
                if (!Succeeded(sessionEnumerator.GetSession(sessionIndex, out session)) || session == null)
                {
                    return;
                }

                var processId = 0;
                string? displayName = null;
                if (session is IAudioSessionControl2 sessionControl2)
                {
                    if (Succeeded(sessionControl2.GetProcessId(out var rawProcessId)))
                    {
                        processId = unchecked((int)rawProcessId);
                    }

                    if (Succeeded(sessionControl2.GetDisplayName(out var reportedDisplayName))
                        && !string.IsNullOrWhiteSpace(reportedDisplayName))
                    {
                        displayName = reportedDisplayName;
                    }

                    if (string.IsNullOrWhiteSpace(displayName)
                        && Succeeded(sessionControl2.GetSessionIdentifier(out var sessionIdentifier))
                        && !string.IsNullOrWhiteSpace(sessionIdentifier))
                    {
                        displayName = sessionIdentifier;
                    }
                }

                var peakLevel = 0f;
                if (session is IAudioMeterInformation meter
                    && Succeeded(meter.GetPeakValue(out var peak)))
                {
                    peakLevel = peak;
                }

                snapshots.Add(new CoreAudioSessionSnapshot(processId, displayName, peakLevel));
            }
            catch
            {
                // The session ended while it was being queried.
            }
            finally
            {
                ReleaseComObject(session);
            }
        }

        private static bool Succeeded(int hresult) => hresult >= 0;

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.ReleaseComObject(value);
                }
                catch
                {
                    // The runtime can have already released an invalidated device.
                }
            }
        }

        private enum EDataFlow
        {
            Render = 0,
            Capture = 1,
            All = 2
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out int deviceCount);

            [PreserveSig]
            int Item(int deviceNumber, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(
                ref Guid interfaceId,
                uint clsContext,
                IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig]
            int GetAudioSessionControl(
                [Optional, In, MarshalAs(UnmanagedType.LPStruct)] Guid sessionId,
                uint streamFlags,
                out IAudioSessionControl sessionControl);

            [PreserveSig]
            int GetSimpleAudioVolume(
                [Optional, In, MarshalAs(UnmanagedType.LPStruct)] Guid sessionId,
                uint streamFlags,
                out object audioVolume);

            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int sessionCount);

            [PreserveSig]
            int GetSession(int sessionIndex, out IAudioSessionControl session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl { }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            [PreserveSig]
            int GetState(out int state);

            [PreserveSig]
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

            [PreserveSig]
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

            [PreserveSig]
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

            [PreserveSig]
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

            [PreserveSig]
            int GetGroupingParam(out Guid groupingId);

            [PreserveSig]
            int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

            [PreserveSig]
            int RegisterAudioSessionNotification(IntPtr client);

            [PreserveSig]
            int UnregisterAudioSessionNotification(IntPtr client);

            [PreserveSig]
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

            [PreserveSig]
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

            [PreserveSig]
            int GetProcessId(out uint processId);

            [PreserveSig]
            int IsSystemSoundsSession();

            [PreserveSig]
            int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport]
        [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig]
            int GetPeakValue(out float peakValue);
        }
    }
