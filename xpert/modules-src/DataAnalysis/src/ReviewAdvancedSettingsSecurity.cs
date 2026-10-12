using System;
using System.Security.Cryptography;
using System.Text;

namespace HVX.PdReview
{
    // This is an operator UI lock, not a security boundary against trusted
    // host callers that can set public control properties directly.
    internal static class ReviewAdvancedSettingsSecurity
    {
#if MAINTENANCE_PASSWORD_GUARD
        internal const bool Enabled = true;
#else
        internal const bool Enabled = false;
#endif
        // SHA-256 of the operator-provided fixed password; never store plaintext.
        const string PasswordSha256="6C005E779FFA02F10A7E03CD1DF7E712EFBCA7315E7158612871087914BF48DD";

        internal static bool VerifyPassword(string candidate)
        {
            if(String.IsNullOrEmpty(candidate)||PasswordSha256.Length!=64)return false;
            byte[] actual;
            using(var sha=SHA256.Create())actual=sha.ComputeHash(Encoding.UTF8.GetBytes(candidate));
            int mismatch=0;
            for(int i=0;i<actual.Length;i++)
            {
                byte expected=Convert.ToByte(PasswordSha256.Substring(i*2,2),16);
                mismatch|=actual[i]^expected;
            }
            return mismatch==0;
        }
    }
}
