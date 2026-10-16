using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SignalWatchdogs.Native
{
    public sealed class PnpDevice
    {
        public string InstanceId;
        public string Name;
        public string Class;
        public string ClassGuid;
        public string Service;
        public string Manufacturer;
        public string BusName;
        public string Location;
        public uint Status;
        public uint Problem;
        public DateTime FirstSeen = DateTime.Now;

        public bool HasProblem => (Status & CfgMgr.DN_HAS_PROBLEM) != 0 && Problem != 0;
        public bool Started => (Status & CfgMgr.DN_STARTED) != 0;
        public string DisplayName => !string.IsNullOrEmpty(BusName) && (string.IsNullOrEmpty(Name) || Name.StartsWith("Périphérique") || Name.StartsWith("USB ") || Name.Contains("générique") || Name.StartsWith("Generic"))
            ? $"{Name} ({BusName})" : (Name ?? InstanceId);
        public string StateText => HasProblem ? CfgMgr.ProblemText(Problem) : Started ? "OK" : "Non démarré";
    }

    /// <summary>Énumération rapide des périphériques via cfgmgr32 (beaucoup plus léger que WMI).</summary>
    public static class CfgMgr
    {
        public const uint DN_STARTED = 0x00000008;
        public const uint DN_HAS_PROBLEM = 0x00000400;
        const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
        const int CR_SUCCESS = 0;
        const int CR_BUFFER_SMALL = 0x1A;

        const uint CM_DRP_DEVICEDESC = 0x1;
        const uint CM_DRP_SERVICE = 0x5;
        const uint CM_DRP_CLASS = 0x8;
        const uint CM_DRP_CLASSGUID = 0x9;
        const uint CM_DRP_MFG = 0xC;
        const uint CM_DRP_FRIENDLYNAME = 0xD;
        const uint CM_DRP_LOCATION_INFORMATION = 0xE;

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        static DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new DEVPROPKEY { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_ID_ListW(string pszFilter, char[] buffer, uint bufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType, byte[] buffer, ref uint pulLength, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType, byte[] propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

        public static List<string> PresentDeviceIds()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS) break;
                var buf = new char[len + 1024];
                int cr = CM_Get_Device_ID_ListW(null, buf, (uint)buf.Length, CM_GETIDLIST_FILTER_PRESENT);
                if (cr == CR_BUFFER_SMALL) continue;
                if (cr != CR_SUCCESS) break;
                var res = new List<string>(2048);
                int start = 0;
                for (int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] != '\0') continue;
                    if (i == start) break;
                    res.Add(new string(buf, start, i - start));
                    start = i + 1;
                }
                return res;
            }
            return new List<string>();
        }

        public static bool TryGetStatus(string instanceId, out uint status, out uint problem)
        {
            status = problem = 0;
            if (CM_Locate_DevNodeW(out uint inst, instanceId, 0) != CR_SUCCESS) return false;
            return CM_Get_DevNode_Status(out status, out problem, inst, 0) == CR_SUCCESS;
        }

        public static PnpDevice Get(string instanceId, bool details = true)
        {
            if (CM_Locate_DevNodeW(out uint inst, instanceId, 0) != CR_SUCCESS) return null;
            var d = new PnpDevice { InstanceId = instanceId.ToUpperInvariant() };
            if (CM_Get_DevNode_Status(out uint st, out uint prob, inst, 0) == CR_SUCCESS)
            {
                d.Status = st;
                d.Problem = prob;
            }
            d.Class = RegProp(inst, CM_DRP_CLASS);
            d.ClassGuid = RegProp(inst, CM_DRP_CLASSGUID)?.ToLowerInvariant();
            d.Name = CleanName(RegProp(inst, CM_DRP_FRIENDLYNAME) ?? RegProp(inst, CM_DRP_DEVICEDESC));
            if (details)
            {
                d.Service = RegProp(inst, CM_DRP_SERVICE);
                d.Manufacturer = CleanName(RegProp(inst, CM_DRP_MFG));
                d.Location = RegProp(inst, CM_DRP_LOCATION_INFORMATION);
                d.BusName = StrProp(inst, DEVPKEY_Device_BusReportedDeviceDesc);
            }
            return d;
        }

        static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Format "@usb.inf,%usb\\...%;Generic USB Hub"
            int semi = s.LastIndexOf(';');
            if (s.StartsWith("@") && semi > 0) s = s.Substring(semi + 1);
            return s.Trim();
        }

        static string RegProp(uint inst, uint prop)
        {
            uint len = 512;
            var buf = new byte[len];
            int cr = CM_Get_DevNode_Registry_PropertyW(inst, prop, out _, buf, ref len, 0);
            if (cr == CR_BUFFER_SMALL)
            {
                buf = new byte[len];
                cr = CM_Get_DevNode_Registry_PropertyW(inst, prop, out _, buf, ref len, 0);
            }
            if (cr != CR_SUCCESS || len < 2) return null;
            return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0').Split('\0')[0];
        }

        static string StrProp(uint inst, DEVPROPKEY key)
        {
            try
            {
                uint len = 512;
                var buf = new byte[len];
                int cr = CM_Get_DevNode_PropertyW(inst, ref key, out uint type, buf, ref len, 0);
                if (cr != CR_SUCCESS || type != 0x12 /* DEVPROP_TYPE_STRING */ || len < 2) return null;
                return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
            }
            catch { return null; }
        }

        public static string ProblemText(uint code) => code switch
        {
            1 => "Mal configuré (code 1)",
            3 => "Pilote endommagé ou mémoire insuffisante (code 3)",
            10 => "Le périphérique ne peut pas démarrer (code 10)",
            12 => "Ressources insuffisantes (code 12)",
            14 => "Redémarrage nécessaire (code 14)",
            18 => "Pilotes à réinstaller (code 18)",
            19 => "Configuration du registre endommagée (code 19)",
            21 => "Suppression en cours (code 21)",
            22 => "Désactivé (code 22)",
            24 => "Absent ou mal installé (code 24)",
            28 => "Pilote non installé (code 28)",
            29 => "Désactivé par le firmware (code 29)",
            31 => "Le pilote ne peut pas être chargé (code 31)",
            32 => "Service du pilote désactivé (code 32)",
            37 => "Échec d'initialisation du pilote (code 37)",
            38 => "Instance précédente du pilote encore en mémoire (code 38)",
            39 => "Pilote endommagé ou manquant (code 39)",
            41 => "Pilote chargé mais matériel introuvable (code 41)",
            43 => "Arrêté : le matériel a signalé un problème (code 43)",
            45 => "Non connecté actuellement (code 45)",
            47 => "Préparé pour le retrait (code 47)",
            48 => "Pilote bloqué (incompatibilité) (code 48)",
            52 => "Signature du pilote non vérifiée (code 52)",
            54 => "Échec de réinitialisation ACPI (code 54)",
            _ => $"Problème (code {code})"
        };
    }
}
