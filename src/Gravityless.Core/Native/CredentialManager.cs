using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Gravityless.Core.Native;

public static class CredentialManager
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);

    public const string DefaultTarget = "gemini:antigravity";
    public const string DefaultUsername = "antigravity";

    /// <summary>
    /// Serializes access to the global credential slot. The target name is fixed by Antigravity,
    /// so only one account can occupy it at a time; callers take this lock around read/write pairs.
    /// </summary>
    public static readonly SemaphoreSlim SlotLock = new(1, 1);

    /// <summary>Default wait budget when acquiring <see cref="SlotLock"/>.</summary>
    public static readonly TimeSpan SlotLockTtl = TimeSpan.FromSeconds(10);

    /// <summary>Reads the generic credential blob for <paramref name="target"/> as UTF-8.</summary>
    public static string? ReadCredential(string target = DefaultTarget)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
                return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="blob"/> looks like a usable Antigravity credential:
    /// a JSON object carrying either a nested <c>token.access_token</c> or a flat <c>access_token</c>.
    /// The slot is global and shared by every instance, so a truncated write must never land there.
    /// </summary>
    public static bool IsValidCredentialBlob(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob) || blob.Length < 64) return false;

        try
        {
            using var doc = JsonDocument.Parse(blob);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            var root = doc.RootElement;
            if (root.TryGetProperty("token", out var nested)
                && nested.ValueKind == JsonValueKind.Object
                && nested.TryGetProperty("access_token", out var nestedToken)
                && nestedToken.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(nestedToken.GetString()))
            {
                return true;
            }

            if (root.TryGetProperty("access_token", out var flatToken)
                && flatToken.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(flatToken.GetString()))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // Malformed payloads are rejected: the caller keeps the previous slot value.
        }

        return false;
    }

    /// <summary>
    /// Writes a UTF-8 blob to the generic credential <paramref name="target"/>.
    /// Rejects payloads that fail <see cref="IsValidCredentialBlob"/> so a partial write
    /// cannot overwrite a valid credential.
    /// </summary>
    public static bool WriteCredential(string blobContent, string target = DefaultTarget, string username = DefaultUsername)
    {
        if (!IsValidCredentialBlob(blobContent)) return false;

        var bytes = Encoding.UTF8.GetBytes(blobContent);
        var blobPtr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = username,
                CredentialBlob = blobPtr,
                CredentialBlobSize = bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE
            };

            return CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    /// <summary>
    /// Clears <paramref name="target"/> only when it still holds the blob for
    /// <paramref name="expectedEmail"/>. Guards against deleting another account's
    /// credential after the slot has been rotated by a concurrent launch.
    /// </summary>
    public static bool ClearSlotIfOwnedBy(string? expectedEmail, string target = DefaultTarget)
    {
        if (string.IsNullOrWhiteSpace(expectedEmail)) return false;

        var current = ReadCredential(target);
        if (string.IsNullOrEmpty(current)) return false;

        // The slot is global; only drop it when it is provably this account's token.
        if (!current.Contains(expectedEmail, StringComparison.OrdinalIgnoreCase)) return false;

        return DeleteCredential(target);
    }

    public static bool DeleteCredential(string target = DefaultTarget)
    {
        return CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    /// <summary>Returns the current blob and deletes the credential.</summary>
    public static string? BackupAndClear(string target = DefaultTarget)
    {
        var existing = ReadCredential(target);
        if (!string.IsNullOrEmpty(existing))
        {
            DeleteCredential(target);
        }
        return existing;
    }

    /// <summary>Writes <paramref name="backupBlob"/> back; deletes the target when the backup is empty.</summary>
    public static void Restore(string? backupBlob, string target = DefaultTarget, string username = DefaultUsername)
    {
        if (!string.IsNullOrEmpty(backupBlob))
        {
            WriteCredential(backupBlob, target, username);
        }
        else
        {
            DeleteCredential(target);
        }
    }
}
