using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SignalWatchdogs.Core
{
    /// <summary>
    /// Garde-fous liés aux privilèges : un processus administrateur ne doit ni s'exécuter depuis,
    /// ni lire/écrire dans un emplacement qu'un utilisateur standard peut modifier.
    /// </summary>
    public static class SecurityGuard
    {
        public static readonly bool IsElevated = ComputeElevated();

        static bool ComputeElevated()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // Droits permettant de créer, remplacer ou supprimer un fichier, ou de changer les permissions.
        const int DangerousRights = 0x2 /* WriteData/CreateFiles */ | 0x4 /* AppendData/CreateDirectories */ | 0x40 /* DeleteChild */
                                   | 0x10000 /* Delete */ | 0x40000 /* WriteDAC */ | 0x80000 /* WriteOwner */
                                   | 0x40000000 /* GENERIC_WRITE */ | 0x10000000 /* GENERIC_ALL */;

        static bool Trusted(SecurityIdentifier sid)
        {
            if (sid == null) return false;
            var v = sid.Value;
            return v == "S-1-5-32-544"                                                   // Administrateurs
                || v == "S-1-5-18"                                                       // SYSTEM
                || v == "S-1-3-0"                                                        // CREATOR OWNER (ne donne des droits que sur ce qu'on a pu créer)
                || v == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; // TrustedInstaller
        }

        static string Name(SecurityIdentifier sid)
        {
            try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
        }

        /// <summary>
        /// Null si seuls les administrateurs / SYSTEM / TrustedInstaller peuvent modifier l'exécutable et son dossier ;
        /// sinon la raison (en français).
        /// </summary>
        public static string WhyExecutableNotProtected(string exePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (dir.StartsWith(@"\\")) return "l'exécutable est sur un partage réseau";
                var root = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType != DriveType.Fixed) return "l'exécutable est sur un lecteur amovible ou réseau";
                if (HasReparsePoint(dir)) return "le dossier de l'exécutable passe par un lien (jonction / lien symbolique)";

                var dirSec = Directory.GetAccessControl(dir);
                var reason = Check(dirSec, dir, folder: true);
                if (reason != null) return reason;
                var fileSec = File.GetAccessControl(exePath);
                return Check(fileSec, exePath, folder: false);
            }
            catch (Exception ex) { return "impossible de vérifier les droits (" + ex.Message + ")"; }
        }

        /// <summary>
        /// Null si seuls les administrateurs / SYSTEM peuvent écrire dans ce dossier local (ou, s'il n'existe pas encore,
        /// dans son premier parent existant), y compris dans les sous-dossiers qui y seront créés.
        /// </summary>
        public static string WhyDirectoryNotProtected(string dir)
        {
            try
            {
                var d = new DirectoryInfo(Path.GetFullPath(dir));
                while (d != null && !d.Exists) d = d.Parent;
                if (d == null) return "dossier introuvable";
                if (HasReparsePoint(d.FullName)) return "le dossier passe par une jonction ou un lien symbolique";
                return Check(Directory.GetAccessControl(d.FullName), d.FullName, folder: true, includeInheritOnly: true);
            }
            catch (Exception ex) { return "impossible de vérifier les droits (" + ex.Message + ")"; }
        }

        static string Check(FileSystemSecurity sec, string path, bool folder, bool includeInheritOnly = false)
        {
            if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !Trusted(owner))
                return $"{Name(owner)} est propriétaire de {path}";
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                if (folder && !includeInheritOnly && (r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if (((int)r.FileSystemRights & DangerousRights) == 0) continue;
                var sid = r.IdentityReference as SecurityIdentifier;
                if (Trusted(sid)) continue;
                return $"{Name(sid)} peut modifier {path}";
            }
            return null;
        }

        /// <summary>Vrai si le chemin (ou un de ses dossiers parents existants) est une jonction / un lien.</summary>
        public static bool HasReparsePoint(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetPathRoot(full);
                for (var d = new DirectoryInfo(full); d != null && !string.Equals(d.FullName.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); d = d.Parent)
                    if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch { }
            return false;
        }

        /// <summary>Supprime les caractères de contrôle et de bidirectionnalité (injection de lignes dans le journal texte).</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                bool bidi = (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩') || c == '‎' || c == '‏' || c == '؜';
                sb.Append(char.IsControl(c) || bidi ? ' ' : c);
            }
            return sb.ToString();
        }
    }
}
