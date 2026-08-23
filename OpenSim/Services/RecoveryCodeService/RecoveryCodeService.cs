using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.RecoveryCodeService
{
    // Backing service for the WebInterface's account-recovery codes - see
    // IRecoveryCodeService/IRecoveryCodeData for the design rationale.
    public class RecoveryCodeService : RecoveryCodeServiceBase, IRecoveryCodeService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        // Excludes 0/O and 1/I/L - the same ambiguity-avoidance every
        // real-world backup-code system (and this grid's own password
        // rules elsewhere) already applies to anything a resident has to
        // transcribe by hand.
        private const string CodeCharset = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        private const int CodeLength = 10;
        private const int CodeCount = 5;
        private const int HashSizeBytes = 32;
        private const int SaltSizeBytes = 16;
        private const int Iterations = 100_000;

        public RecoveryCodeService(IConfigSource config)
            : base(config)
        {
            m_log.Debug("[RECOVERY CODE SERVICE]: Starting recovery code service");
        }

        public List<string> RegenerateCodes(UUID principalID)
        {
            m_Database.DeleteAllForPrincipal(principalID);

            List<string> plaintextCodes = new List<string>(CodeCount);
            for (int i = 0; i < CodeCount; i++)
            {
                string code = GenerateCode();
                plaintextCodes.Add(code);

                byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
                byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(NormalizeCode(code)), salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

                m_Database.Store(new RecoveryCode
                {
                    ID = UUID.Random(),
                    PrincipalID = principalID,
                    CodeHash = Convert.ToBase64String(hash),
                    CodeSalt = Convert.ToBase64String(salt),
                    Used = false,
                    Created = DateTime.UtcNow
                });
            }

            return plaintextCodes;
        }

        public int GetRemainingCount(UUID principalID)
        {
            List<RecoveryCode> codes = m_Database.GetByPrincipal(principalID);
            return codes.FindAll(c => !c.Used).Count;
        }

        public bool RedeemCode(UUID principalID, string code)
        {
            string normalized = NormalizeCode(code);
            if (string.IsNullOrEmpty(normalized))
                return false;

            List<RecoveryCode> codes = m_Database.GetByPrincipal(principalID);
            foreach (RecoveryCode stored in codes)
            {
                if (stored.Used)
                    continue;

                byte[] salt = Convert.FromBase64String(stored.CodeSalt);
                byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalized), salt, Iterations, HashAlgorithmName.SHA256, HashSizeBytes);

                if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(stored.CodeHash)))
                    return m_Database.MarkUsed(stored.ID);
            }

            return false;
        }

        // Case/whitespace/dash-insensitive - matches how the codes are
        // displayed (grouped for readability) so a resident retyping one
        // isn't tripped up by formatting that was never meant to be
        // significant.
        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return string.Empty;

            StringBuilder sb = new StringBuilder(code.Length);
            foreach (char c in code)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        private static string GenerateCode()
        {
            char[] chars = new char[CodeLength];
            for (int i = 0; i < CodeLength; i++)
                chars[i] = CodeCharset[RandomNumberGenerator.GetInt32(CodeCharset.Length)];
            return new string(chars);
        }
    }
}
