using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MediaDevices;

namespace BackupService.FileSystem.Mtp
{
    /// <summary>
    /// Reads a file's MTP/WPD <b>Object Modification Time</b> (else <b>Object Creation Time</b>) directly from the
    /// device's property store and parses it — a true property read (no file transfer), the same data Explorer shows.
    /// <para>
    /// Why this exists: <see cref="MediaFileInfo.LastWriteTime"/>/<see cref="MediaFileInfo.CreationTime"/> come back
    /// <c>null</c> for many cameras (e.g. a Sony A6700 exposes only <c>DATE_CREATED</c>, as a <c>VT_DATE</c>, and
    /// MediaDevices doesn't surface it). We reach MediaDevices' already-connected WPD property reader by reflection,
    /// enumerate the object's properties, and pick the WPD date keys ourselves — handling whichever form the device
    /// sends (a numeric <c>VT_DATE</c>, or a date string). All reflection is cached and every failure degrades to
    /// <c>null</c> (the caller falls back to EXIF), so it can never do worse than before.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class MtpObjectDate
    {
        // WPD_OBJECT_PROPERTIES_V1 — the standard WPD object property category. DATE_MODIFIED is pid 19, DATE_CREATED 18.
        private static readonly Guid WpdObjectCategory = new("ef6b490d-5cd8-437a-affc-da8b60ee4a3c");
        private const uint PidDateModified = 19;
        private const uint PidDateCreated = 18;

        private static readonly FieldInfo? DevicePropertiesField;   // MediaDevice.deviceProperties (IPortableDeviceProperties)
        private static readonly FieldInfo? FileInfoItemField;       // MediaFileInfo.item (Internal.Item)
        private static readonly PropertyInfo? ItemIdProperty;       // Internal.Item.Id (the WPD object id)
        private static readonly MethodInfo? GetValuesMethod;        // IPortableDeviceProperties.GetValues
        private static readonly MethodInfo? GetCountMethod;         // IPortableDeviceValues.GetCount
        private static readonly MethodInfo? GetAtMethod;            // IPortableDeviceValues.GetAt
        private static readonly FieldInfo? PvVtField;               // PropVariant.vt
        private static readonly FieldInfo? PvPtrField;              // PropVariant.ptrVal
        private static readonly FieldInfo? PvDateField;             // PropVariant.dateVal
        private static readonly FieldInfo? PkFmtidField;            // PropertyKey.fmtid
        private static readonly FieldInfo? PkPidField;              // PropertyKey.pid

        internal static bool Available { get; }

        static MtpObjectDate()
        {
            try
            {
                var asm = typeof(MediaDevice).Assembly;
                var bind = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

                DevicePropertiesField = typeof(MediaDevice).GetField("deviceProperties", bind);
                FileInfoItemField = typeof(MediaFileInfo).GetField("item", bind);
                ItemIdProperty = asm.GetType("MediaDevices.Internal.Item")?.GetProperty("Id", bind);
                GetValuesMethod = asm.GetType("MediaDevices.Internal.IPortableDeviceProperties")?.GetMethod("GetValues");

                var ipdValues = asm.GetType("MediaDevices.Internal.IPortableDeviceValues");
                GetCountMethod = ipdValues?.GetMethod("GetCount");
                GetAtMethod = ipdValues?.GetMethod("GetAt");

                var propVariant = asm.GetType("MediaDevices.Internal.PropVariant");
                PvVtField = propVariant?.GetField("vt", bind);
                PvPtrField = propVariant?.GetField("ptrVal", bind);
                PvDateField = propVariant?.GetField("dateVal", bind);

                var propertyKey = asm.GetType("MediaDevices.Internal.PropertyKey");
                PkFmtidField = propertyKey?.GetField("fmtid", bind);
                PkPidField = propertyKey?.GetField("pid", bind);

                Available = DevicePropertiesField is not null && FileInfoItemField is not null
                    && ItemIdProperty is not null && GetValuesMethod is not null
                    && GetCountMethod is not null && GetAtMethod is not null
                    && PvVtField is not null && PvPtrField is not null && PvDateField is not null
                    && PkFmtidField is not null && PkPidField is not null;
            }
            catch
            {
                Available = false;
            }
        }

        /// <summary>
        /// The object's modification (else creation) time as UTC-kind wall-clock — matching how the other date
        /// sources are normalised so a round-trip compares equal — or null if unavailable/unparseable.
        /// </summary>
        public static DateTime? TryRead(MediaDevice device, MediaFileInfo info)
        {
            if (!Available)
            {
                return null;
            }

            try
            {
                if (GetValues(device, info) is not { } values)
                {
                    return null;
                }

                DateTime? modified = null;
                DateTime? created = null;
                Enumerate(values, (key, pv) =>
                {
                    if (!IsWpdObjectDateKey(key, out var pid))
                    {
                        return;
                    }
                    if (pid == PidDateModified)
                    {
                        modified = VariantToDate(pv);
                    }
                    else if (pid == PidDateCreated)
                    {
                        created = VariantToDate(pv);
                    }
                });

                return modified ?? created;
            }
            catch
            {
                return null;
            }
        }

        private static object? GetValues(MediaDevice device, MediaFileInfo info)
        {
            var item = FileInfoItemField!.GetValue(info);
            if (item is null || ItemIdProperty!.GetValue(item) is not string objectId || string.IsNullOrEmpty(objectId))
            {
                return null;
            }

            var properties = DevicePropertiesField!.GetValue(device);
            if (properties is null)
            {
                return null;
            }

            // GetValues(objectId, pKeys: null, out values) — null keys returns all property values for the object.
            var args = new object?[] { objectId, null, null };
            GetValuesMethod!.Invoke(properties, args);
            return args[2];
        }

        // Walks every (key, value) pair in a property bag via GetCount/GetAt (the typed GetValue overload wants a
        // by-ref PropertyKey that's awkward to pass by reflection; GetAt avoids it entirely).
        private static void Enumerate(object values, Action<object, object?> visit)
        {
            var countArgs = new object?[] { (uint)0 };
            GetCountMethod!.Invoke(values, countArgs);
            var count = (uint)countArgs[0]!;

            for (uint i = 0; i < count; i++)
            {
                var atArgs = new object?[] { i, null, null };
                GetAtMethod!.Invoke(values, atArgs);
                if (atArgs[1] is { } key)
                {
                    visit(key, atArgs[2]);
                }
            }
        }

        private static bool IsWpdObjectDateKey(object key, out uint pid)
        {
            pid = 0;
            if (PkFmtidField!.GetValue(key) is Guid fmtid && fmtid == WpdObjectCategory
                && PkPidField!.GetValue(key) is uint p)
            {
                pid = p;
                return true;
            }
            return false;
        }

        // Reads a date PropVariant, handling the numeric (VT_DATE) and string (VT_LPWSTR/LPSTR/BSTR) forms a camera
        // might send. UTC-kind wall-clock to match how the other date sources are normalised.
        private static DateTime? VariantToDate(object? pv)
        {
            if (pv is null)
            {
                return null;
            }

            var vt = PvVtField!.GetValue(pv)?.ToString();
            switch (vt)
            {
                case "VT_DATE":
                    try { return DateTime.SpecifyKind(DateTime.FromOADate((double)PvDateField!.GetValue(pv)!), DateTimeKind.Utc); }
                    catch { return null; }
                case "VT_LPWSTR":
                    return MtpDateString.Parse(Marshal.PtrToStringUni((IntPtr)PvPtrField!.GetValue(pv)!));
                case "VT_LPSTR":
                    return MtpDateString.Parse(Marshal.PtrToStringAnsi((IntPtr)PvPtrField!.GetValue(pv)!));
                case "VT_BSTR":
                    return MtpDateString.Parse(Marshal.PtrToStringBSTR((IntPtr)PvPtrField!.GetValue(pv)!));
                default:
                    return null;
            }
        }
    }
}
