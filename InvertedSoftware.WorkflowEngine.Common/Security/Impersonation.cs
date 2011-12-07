// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
//
// Copyright (C) Inverted Software(TM). All rights reserved.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Security.Principal;
using System.Runtime.InteropServices;

namespace InvertedSoftware.WorkflowEngine.Common.Security
{
	public enum ImpersonationLogonType
	{
		LOGON32_LOGON_INTERACTIVE = 2,
		LOGON32_LOGON_NETWORK = 3,
		LOGON32_LOGON_BATCH = 4,
		LOGON32_LOGON_SERVICE = 5,
		LOGON32_LOGON_UNLOCK = 7,
		LOGON32_LOGON_NETWORK_CLEARTEXT = 8,
		LOGON32_LOGON_NEW_CREDENTIALS = 9
	}

	public enum ImpersonationProviderType
	{
		LOGON32_PROVIDER_DEFAULT = 0,
		LOGON32_PROVIDER_WINNT50 = 3,
		LOGON32_PROVIDER_WINNT40 = 2,
		LOGON32_PROVIDER_WINNT35 = 1
	}

	/// <summary>
	/// Utility class to help with Impersonation
	/// </summary>
	public class Impersonation
	{
		// Base code from http://support.microsoft.com/kb/306158

		private const int LOGON32_PROVIDER_DEFAULT = 0;

		WindowsImpersonationContext impersonationContext;

		public Impersonation()
		{
			ImpersonationLogonType = ImpersonationLogonType.LOGON32_LOGON_INTERACTIVE;
			ImpersonationProviderType = ImpersonationProviderType.LOGON32_PROVIDER_DEFAULT;
		}

		[DllImport("advapi32.dll")]
		private static extern int LogonUserA(String lpszUserName,
			String lpszDomain,
			String lpszPassword,
			int dwLogonType,
			int dwLogonProvider,
			ref IntPtr phToken);

		[DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern int DuplicateToken(IntPtr hToken,
			int impersonationLevel,
			ref IntPtr hNewToken);

		[DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		private static extern bool RevertToSelf();

		[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
		private static extern bool CloseHandle(IntPtr handle);

		/// <summary>
		/// Impersonate a User
		/// </summary>
		/// <param name="userName">The User</param>
		/// <param name="domain">The Domain</param>
		/// <param name="password">The Password</param>
		/// <returns>true if impersonation succeeded</returns>
		public bool ImpersonateValidUser(String userName, String domain, String password)
		{
			WindowsIdentity tempWindowsIdentity;
			IntPtr token = IntPtr.Zero;
			IntPtr tokenDuplicate = IntPtr.Zero;

			if (RevertToSelf())
			{
				if (LogonUserA(userName, domain, password, (int)ImpersonationLogonType,
					(int)ImpersonationProviderType, ref token) != 0)
				{
					if (DuplicateToken(token, 2, ref tokenDuplicate) != 0)
					{
						tempWindowsIdentity = new WindowsIdentity(tokenDuplicate);
						impersonationContext = tempWindowsIdentity.Impersonate();
						if (impersonationContext != null)
						{
							CloseHandle(token);
							CloseHandle(tokenDuplicate);
							return true;
						}
					}
				}
			}
			if (token != IntPtr.Zero)
				CloseHandle(token);
			if (tokenDuplicate != IntPtr.Zero)
				CloseHandle(tokenDuplicate);
			return false;
		}

		/// <summary>
		/// Stop impersonation and return to the current user
		/// </summary>
		public void UndoImpersonation()
		{
			impersonationContext.Undo();
		}

		/// <summary>
		/// The Logon Type. Default LOGON32_LOGON_INTERACTIVE
		/// </summary>
		public ImpersonationLogonType ImpersonationLogonType { get; set; }

		/// <summary>
		/// The provider type to use. Defaults to LOGON32_PROVIDER_DEFAULT
		/// </summary>
		public ImpersonationProviderType ImpersonationProviderType { get; set; }
	}
}
