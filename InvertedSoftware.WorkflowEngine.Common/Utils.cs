using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InvertedSoftware.WorkflowEngine.Common.Security;

namespace InvertedSoftware.WorkflowEngine.Common
{
	public static class Utils
	{
		public static string ENCODING_SALT = "KSDRTLMRT_142IS";
		public static string ENCODING_VECTOR = "@1B2c3D4e5F6g7H8";
		public static string MESSAGE_BASE_TYPE = "InvertedSoftware.WorkflowEngine.Messages";
		public static int PROCESSOR_COUNT = Environment.ProcessorCount;

		/// <summary>
		/// Gets an Encrypted string using the App's Salt
		/// </summary>
		/// <param name="textToEncode">The clear text to encode</param>
		/// <returns>Encoded text</returns>
		public static string GetEncryptedString(string textToEncode)
		{
			string encodedText = string.Empty;
			RijndaelEnhanced rijndaelKey = new RijndaelEnhanced(Utils.ENCODING_SALT, Utils.ENCODING_VECTOR);

			try
			{
				encodedText = rijndaelKey.Encrypt(textToEncode);
			}
			catch (Exception e) { }
			return encodedText;
		}

		/// <summary>
		/// Decodes a Encrypted string using the App's Salt
		/// </summary>
		/// <param name="textToDecode">The encoded text</param>
		/// <returns>Clear text</returns>
		public static string GetDecryptedString(string textToDecode)
		{
			string decodedText = string.Empty;
			RijndaelEnhanced rijndaelKey = new RijndaelEnhanced(Utils.ENCODING_SALT, Utils.ENCODING_VECTOR);

			try
			{
				decodedText = rijndaelKey.Decrypt(textToDecode);
			}
			catch (Exception e) { }
			return decodedText;
		}
	}
}
