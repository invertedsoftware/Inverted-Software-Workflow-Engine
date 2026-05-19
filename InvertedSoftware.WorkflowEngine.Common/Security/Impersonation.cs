// Copyright (c) Inverted Software. All rights reserved.
// Original P/Invoke pattern based on https://support.microsoft.com/kb/306158

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace InvertedSoftware.WorkflowEngine.Common.Security;

public enum ImpersonationLogonType
{
    LOGON32_LOGON_INTERACTIVE = 2,
    LOGON32_LOGON_NETWORK = 3,
    LOGON32_LOGON_BATCH = 4,
    LOGON32_LOGON_SERVICE = 5,
    LOGON32_LOGON_UNLOCK = 7,
    LOGON32_LOGON_NETWORK_CLEARTEXT = 8,
    LOGON32_LOGON_NEW_CREDENTIALS = 9,
}

public enum ImpersonationProviderType
{
    LOGON32_PROVIDER_DEFAULT = 0,
    LOGON32_PROVIDER_WINNT50 = 3,
    LOGON32_PROVIDER_WINNT40 = 2,
    LOGON32_PROVIDER_WINNT35 = 1,
}

/// <summary>
/// Windows-only utility for running a delegate as a different user. Replaces the
/// v1 <c>WindowsIdentity.Impersonate()</c> pattern (removed in net5+) with
/// <see cref="WindowsIdentity.RunImpersonated(SafeAccessTokenHandle, Action)"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Impersonation
{
    public ImpersonationLogonType ImpersonationLogonType { get; set; } = ImpersonationLogonType.LOGON32_LOGON_INTERACTIVE;
    public ImpersonationProviderType ImpersonationProviderType { get; set; } = ImpersonationProviderType.LOGON32_PROVIDER_DEFAULT;

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(
        string lpszUserName,
        string lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out SafeAccessTokenHandle phToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RevertToSelf();

    /// <summary>
    /// Run <paramref name="action"/> in the security context of the supplied credentials.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">Logon failed.</exception>
    public void RunAs(string userName, string domain, string password, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!RevertToSelf())
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RevertToSelf failed.");

        if (!LogonUser(userName, domain, password,
                (int)ImpersonationLogonType, (int)ImpersonationProviderType,
                out SafeAccessTokenHandle token))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                $"LogonUser failed for {domain}\\{userName}.");
        }

        using (token)
            WindowsIdentity.RunImpersonated(token, action);
    }
}
