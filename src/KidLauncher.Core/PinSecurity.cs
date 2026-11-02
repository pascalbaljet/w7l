using System;
using System.Security.Cryptography;

namespace KidLauncher.Core
{
    public static class PinSecurity
    {
        public static void SetPin(ControlSettings settings, string pin)
        {
            if (string.IsNullOrWhiteSpace(pin) || pin.Length < 4) throw new ArgumentException("Use a PIN with at least four characters.");
            var salt = new byte[24];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
            settings.PinSalt = Convert.ToBase64String(salt);
            settings.PinHash = Convert.ToBase64String(Derive(pin, salt));
        }

        public static bool VerifyPin(ControlSettings settings, string pin)
        {
            if (string.IsNullOrEmpty(settings.PinHash) || string.IsNullOrEmpty(settings.PinSalt) || pin == null) return false;
            try
            {
                var expected = Convert.FromBase64String(settings.PinHash);
                var actual = Derive(pin, Convert.FromBase64String(settings.PinSalt));
                var different = expected.Length ^ actual.Length;
                for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++) different |= expected[i] ^ actual[i];
                return different == 0;
            }
            catch (FormatException) { return false; }
        }

        private static byte[] Derive(string pin, byte[] salt)
        {
            using (var algorithm = new Rfc2898DeriveBytes(pin, salt, 100000, HashAlgorithmName.SHA256)) return algorithm.GetBytes(32);
        }
    }
}
