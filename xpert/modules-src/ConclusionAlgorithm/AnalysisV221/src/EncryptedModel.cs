using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HVX.Location
{
    internal sealed class RegionModel
    {
        internal string Version;
        internal int FeatureDim;
        internal int HiddenDim;
        internal double[] Mean;
        internal double[] Scale;
        internal double[] W1;
        internal double[] B1;
        internal double[] W2;
        internal double B2;

        internal double Predict(double[] features)
        {
            double[] hidden = new double[HiddenDim];
            int h;
            int i;
            for (h = 0; h < HiddenDim; h++)
            {
                double sum = B1[h];
                for (i = 0; i < FeatureDim; i++)
                {
                    double z = (features[i] - Mean[i]) / Math.Max(Scale[i], 1e-9);
                    sum += W1[h * FeatureDim + i] * z;
                }
                hidden[h] = Math.Tanh(sum);
            }
            double logit = B2;
            for (h = 0; h < HiddenDim; h++) logit += W2[h] * hidden[h];
            if (logit >= 0.0) return 1.0 / (1.0 + Math.Exp(-Math.Min(logit, 60.0)));
            double exp = Math.Exp(Math.Max(logit, -60.0));
            return exp / (1.0 + exp);
        }
    }

    internal static class EmbeddedModelLoader
    {
        private static byte[] Sha256(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        }

        private static string MasterSecret()
        {
            // Split and transformed to avoid a single obvious plaintext key in the assembly.
            string[] parts = new string[] {
                "0b2D-7Qp9-", "HVX-Location-", "2026-Xpert-", "M8k4-LabVIEW"
            };
            return parts[1] + parts[3] + parts[0] + parts[2];
        }

        internal static RegionModel Load()
        {
            byte[] blob = EmbeddedModelBlob.Data;
            if (blob == null || blob.Length < 5 + 16 + 32) throw new InvalidDataException("嵌入模型不存在");
            string magic = Encoding.ASCII.GetString(blob, 0, 5);
            if (magic != "HVXM1") throw new InvalidDataException("模型文件头错误");
            int cipherLength = blob.Length - 5 - 16 - 32;
            byte[] iv = new byte[16];
            byte[] cipher = new byte[cipherLength];
            byte[] actualTag = new byte[32];
            Buffer.BlockCopy(blob, 5, iv, 0, 16);
            Buffer.BlockCopy(blob, 21, cipher, 0, cipherLength);
            Buffer.BlockCopy(blob, 21 + cipherLength, actualTag, 0, 32);

            byte[] encKey = Sha256(MasterSecret() + "|enc");
            byte[] macKey = Sha256(MasterSecret() + "|mac");
            byte[] signed = new byte[5 + 16 + cipherLength];
            Buffer.BlockCopy(blob, 0, signed, 0, signed.Length);
            byte[] expectedTag;
            using (HMACSHA256 hmac = new HMACSHA256(macKey)) expectedTag = hmac.ComputeHash(signed);
            if (!FixedTimeEquals(actualTag, expectedTag)) throw new CryptographicException("模型完整性校验失败");

            byte[] plain;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            }
            Array.Clear(encKey, 0, encKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            return Parse(Encoding.UTF8.GetString(plain));
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            int i;
            for (i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static RegionModel Parse(string text)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            int i;
            if (lines.Length == 0 || lines[0].Trim() != "HVXMODEL1") throw new InvalidDataException("模型版本头错误");
            for (i = 1; i < lines.Length; i++)
            {
                int equals = lines[i].IndexOf('=');
                if (equals > 0) values[lines[i].Substring(0, equals).Trim()] = lines[i].Substring(equals + 1).Trim();
            }
            RegionModel model = new RegionModel();
            model.Version = Require(values, "version");
            model.FeatureDim = int.Parse(Require(values, "feature_dim"), CultureInfo.InvariantCulture);
            model.HiddenDim = int.Parse(Require(values, "hidden_dim"), CultureInfo.InvariantCulture);
            if (model.FeatureDim != 16) throw new InvalidDataException("模型特征维度必须为16");
            model.Mean = ParseArray(Require(values, "mean"), model.FeatureDim);
            model.Scale = ParseArray(Require(values, "scale"), model.FeatureDim);
            model.W1 = ParseArray(Require(values, "w1"), model.FeatureDim * model.HiddenDim);
            model.B1 = ParseArray(Require(values, "b1"), model.HiddenDim);
            model.W2 = ParseArray(Require(values, "w2"), model.HiddenDim);
            model.B2 = double.Parse(Require(values, "b2"), CultureInfo.InvariantCulture);
            return model;
        }

        private static string Require(Dictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value)) throw new InvalidDataException("模型缺少字段：" + key);
            return value;
        }

        private static double[] ParseArray(string text, int expected)
        {
            string[] parts = text.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != expected) throw new InvalidDataException("模型数组长度错误");
            double[] result = new double[expected];
            int i;
            for (i = 0; i < expected; i++) result[i] = double.Parse(parts[i], CultureInfo.InvariantCulture);
            return result;
        }
    }
}
