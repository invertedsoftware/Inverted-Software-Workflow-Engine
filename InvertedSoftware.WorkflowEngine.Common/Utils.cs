// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Common.Security;

namespace InvertedSoftware.WorkflowEngine.Common;

public static class Utils
{
    /// <summary>Default salt for the encoded RunAs* attributes in Workflow.xml.</summary>
    public const string ENCODING_SALT = "KSDRTLMRT_142IS";

    /// <summary>Default IV for the encoded RunAs* attributes in Workflow.xml.</summary>
    public const string ENCODING_VECTOR = "@1B2c3D4e5F6g7H8";

    /// <summary>Base namespace for built-in <see cref="Messages.IWorkflowMessage"/> implementations.</summary>
    public const string MESSAGE_BASE_TYPE = "InvertedSoftware.WorkflowEngine.Messages";

    public static readonly int PROCESSOR_COUNT = Environment.ProcessorCount;

    /// <summary>Encrypt <paramref name="textToEncode"/> using the engine's default salt/IV.</summary>
    public static string GetEncryptedString(string textToEncode)
    {
        try
        {
            var key = new RijndaelEnhanced(ENCODING_SALT, ENCODING_VECTOR);
            return key.Encrypt(textToEncode);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Decrypt <paramref name="textToDecode"/> using the engine's default salt/IV.</summary>
    public static string GetDecryptedString(string textToDecode)
    {
        try
        {
            var key = new RijndaelEnhanced(ENCODING_SALT, ENCODING_VECTOR);
            return key.Decrypt(textToDecode);
        }
        catch
        {
            return string.Empty;
        }
    }
}
