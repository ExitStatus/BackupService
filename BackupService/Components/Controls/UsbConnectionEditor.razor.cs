using BackupService.Connections.Usb;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a USB connection: pick the connected device to bind to (a mass-storage drive or an MTP portable
    /// device) and a root folder on it. Mutates the shared <see cref="UsbEditModel"/> the hosting dialog reads on save.
    /// </summary>
    public partial class UsbConnectionEditor : ComponentBase, IDisposable
    {
        [Inject]
        private IUsbConnector UsbConnector { get; set; } = default!;

        [Inject]
        private IMtpDeviceInspector MtpInspector { get; set; } = default!;

        [Parameter]
        public UsbEditModel Model { get; set; } = new();

        private const string MassStoragePrefix = "ms:";
        private const string MtpPrefix = "mtp:";

        private List<UsbDevice> _drives = [];
        private List<MtpDevice> _mtpDevices = [];
        private List<string> _keys = [];
        private string? _selectedKey;
        private string? _mountPath;      // mass-storage mount path when the bound drive is connected
        private bool _showLocalBrowser;
        private bool _showMtpBrowser;
        private bool _deviceError;
        private System.Threading.Timer? _pollTimer;

        protected override void OnInitialized()
        {
            RefreshDevices();
            // Poll for device insert/remove while the dialog is open, so the picker stays current without Refresh.
            _pollTimer = new System.Threading.Timer(_ => _ = InvokeAsync(Poll), null,
                TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5));
        }

        private void Poll()
        {
            // Don't re-enumerate while a browser dialog is open over the editor.
            if (_showLocalBrowser || _showMtpBrowser)
            {
                return;
            }

            var before = string.Join("|", _keys);
            RefreshDevices();
            if (before != string.Join("|", _keys))
            {
                StateHasChanged();
            }
        }

        public void Dispose() => _pollTimer?.Dispose();

        private void RefreshDevices()
        {
            _drives = UsbConnector.EnumerateConnectedDevices().ToList();
            _mtpDevices = MtpInspector.EnumerateMtpDevices().ToList();

            _keys = _drives.Select(d => MassStoragePrefix + d.DriveLetter)
                .Concat(_mtpDevices.Select(d => MtpPrefix + d.Serial))
                .ToList();

            // If the model is already bound, find it among the connected devices (for the mount path + status).
            _mountPath = null;
            _selectedKey = null;
            if (Model.Kind == UsbDeviceKind.MassStorage && !string.IsNullOrEmpty(Model.VolumeSerial))
            {
                var info = new UsbConnectionInfo(UsbDeviceKind.MassStorage, Model.HardwareSerial, Model.VolumeSerial, null, Model.RootFolder);
                var bound = _drives.FirstOrDefault(d => UsbDeviceMatcher.Matches(info, d));
                if (bound is not null)
                {
                    _selectedKey = MassStoragePrefix + bound.DriveLetter;
                    _mountPath = bound.MountPath;
                }
            }
            else if (Model.Kind == UsbDeviceKind.Mtp && !string.IsNullOrEmpty(Model.MtpSerial))
            {
                var bound = _mtpDevices.FirstOrDefault(d => UsbDeviceMatcher.MatchesMtp(Model.MtpSerial, d.Serial));
                if (bound is not null)
                {
                    _selectedKey = MtpPrefix + bound.Serial;
                }
            }

            // With exactly one device connected and nothing bound yet, select it automatically.
            if (_selectedKey is null && _keys.Count == 1 && !HasBoundDevice())
            {
                OnDeviceSelected(_keys[0]);
            }
        }

        private bool HasBoundDevice() => Model.Kind == UsbDeviceKind.Mtp
            ? !string.IsNullOrEmpty(Model.MtpSerial)
            : !string.IsNullOrEmpty(Model.VolumeSerial);

        private string DeviceLabel(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "Select a device…";
            }

            if (key.StartsWith(MassStoragePrefix, StringComparison.Ordinal))
            {
                var letter = key[MassStoragePrefix.Length..];
                var device = _drives.FirstOrDefault(d => d.DriveLetter == letter);
                var label = string.IsNullOrEmpty(device?.Label) ? "Removable disk" : device!.Label;
                return $"{label} ({letter})";
            }

            var serial = key[MtpPrefix.Length..];
            var mtp = _mtpDevices.FirstOrDefault(d => d.Serial == serial);
            return $"{mtp?.Name ?? "Portable device"} (MTP)";
        }

        private void OnDeviceSelected(string? key)
        {
            _selectedKey = key;
            if (key is null)
            {
                return;
            }

            if (key.StartsWith(MassStoragePrefix, StringComparison.Ordinal))
            {
                var device = _drives.FirstOrDefault(d => d.DriveLetter == key[MassStoragePrefix.Length..]);
                if (device is null)
                {
                    return;
                }

                Model.Kind = UsbDeviceKind.MassStorage;
                Model.HardwareSerial = device.HardwareSerial;
                Model.VolumeSerial = device.VolumeSerial;
                Model.MtpSerial = null;
                Model.DeviceLabel = device.Label;
                _mountPath = device.MountPath;
            }
            else
            {
                var device = _mtpDevices.FirstOrDefault(d => d.Serial == key[MtpPrefix.Length..]);
                if (device is null)
                {
                    return;
                }

                Model.Kind = UsbDeviceKind.Mtp;
                Model.HardwareSerial = null;
                Model.VolumeSerial = string.Empty;
                Model.MtpSerial = device.Serial;
                Model.DeviceLabel = device.Name;
                _mountPath = null;
            }
        }

        /// <summary>Requires a device to have been chosen (identity captured for its kind).</summary>
        public bool Validate()
        {
            _deviceError = Model.Kind == UsbDeviceKind.Mtp
                ? string.IsNullOrEmpty(Model.MtpSerial)
                : string.IsNullOrEmpty(Model.VolumeSerial);
            return !_deviceError;
        }

        private bool IsConnected => Model.Kind == UsbDeviceKind.Mtp
            ? !string.IsNullOrEmpty(Model.MtpSerial) && _mtpDevices.Any(d => UsbDeviceMatcher.MatchesMtp(Model.MtpSerial, d.Serial))
            : _mountPath is not null;

        private string StatusText
        {
            get
            {
                if (IsConnected)
                {
                    return Model.Kind == UsbDeviceKind.Mtp
                        ? $"Connected ({(string.IsNullOrEmpty(Model.DeviceLabel) ? "portable device" : Model.DeviceLabel)})."
                        : $"Connected at {_mountPath}.";
                }

                var bound = Model.Kind == UsbDeviceKind.Mtp ? !string.IsNullOrEmpty(Model.MtpSerial) : !string.IsNullOrEmpty(Model.VolumeSerial);
                return bound
                    ? $"Bound device '{(string.IsNullOrEmpty(Model.DeviceLabel) ? "device" : Model.DeviceLabel)}' is not currently connected."
                    : "Plug the device in and select it above.";
            }
        }

        private string? LocalBrowseInitialPath => _mountPath is null
            ? null
            : string.IsNullOrEmpty(Model.RootFolder) ? _mountPath : Path.Combine(_mountPath, Model.RootFolder);

        private void Browse()
        {
            if (!IsConnected)
            {
                return;
            }

            if (Model.Kind == UsbDeviceKind.Mtp)
            {
                _showMtpBrowser = true;
            }
            else
            {
                _showLocalBrowser = true;
            }
        }

        private void OnLocalFolderSelected(string absolutePath)
        {
            if (_mountPath is not null)
            {
                var relative = Path.GetRelativePath(_mountPath, absolutePath);
                Model.RootFolder = relative is "." or "" ? null : relative;
            }

            CloseBrowser();
        }

        // The MTP browser (no confine) returns a device-absolute path; that is the connection's root.
        private void OnMtpFolderSelected(string deviceAbsolutePath)
        {
            Model.RootFolder = string.IsNullOrEmpty(deviceAbsolutePath) || deviceAbsolutePath == @"\" ? null : deviceAbsolutePath;
            CloseBrowser();
        }

        private void CloseBrowser()
        {
            _showLocalBrowser = false;
            _showMtpBrowser = false;
        }

        /// <summary>Editable USB fields shared between this control and its hosting dialog.</summary>
        public sealed class UsbEditModel
        {
            public UsbDeviceKind Kind { get; set; } = UsbDeviceKind.MassStorage;

            public string? HardwareSerial { get; set; }

            public string VolumeSerial { get; set; } = string.Empty;

            public string? MtpSerial { get; set; }

            public string? DeviceLabel { get; set; }

            public string? RootFolder { get; set; }
        }
    }
}
