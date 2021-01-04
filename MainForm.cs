using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Windows.Forms;
using ZenStates.Components;
using ZenStates.Core;
using ZenStates.Utils;

namespace ZenStates
{
    public partial class AppWindow : Form
    {
        //TODO: TlbCacheDis, MSRC001_1004
        // MSR
        private const uint MSR_PStateCurLim = 0xC0010061; // [6:4] PstateMaxVal
        private const uint MSR_PStateStat = 0xC0010063; // [2:0] CurPstate
        private const uint MSR_PStateDef0 = 0xC0010064; // [63] PstateEn [21:14] CpuVid [13:8] CpuDfsId [7:0] CpuFid
        private const uint MSR_PMGT_MISC = 0xC0010292; // [32] PC6En
        private const uint MSR_PSTATE_BOOST = 0xC0010293;
        private const uint MSR_CSTATE_CONFIG = 0xC0010296; // [22] CCR2_CC6EN [14] CCR1_CC6EN [6] CCR0_CC6EN
        private const uint MSR_HWCR = 0xC0010015;
        private const uint MSR_PERFBIAS1 = 0xC0011020;
        private const uint MSR_PERFBIAS2 = 0xC0011021;
        private const uint MSR_PERFBIAS3 = 0xC001102B; // bit 0 - prefetch?
        private const uint MSR_PERFBIAS4 = 0xC001102D;
        private const uint MSR_PERFBIAS5 = 0xC0011093;

        // Thermal
        private const uint THM_TCON_CUR_TMP = 0x00059800;
        private const uint THM_TCON_PROCHOT = 0x00059804;
        private const uint THM_TCON_THERM_TRIP = 0x00059808;

        private readonly uint dramBaseAddress = 0;

        private enum PerfBias { Auto = 0, None, Cinebench_R11p5, Cinebench_R15, Geekbench_3, SuperPi };
        private enum PerfEnh { Auto = 0, Default, Level1, Level2, Level3_OC, Level4_OC };

        private static readonly Dictionary<PerfBias, string> PerfBiasDict = new Dictionary<PerfBias, string>
        {
            { PerfBias.Auto, "Auto" },
            { PerfBias.None, "None" },
            { PerfBias.Cinebench_R11p5, "Cinebench R11.5" },
            { PerfBias.Cinebench_R15, "Cinebench R15 / R20" },
            { PerfBias.Geekbench_3, "Geekbench 3 / AIDA64" },
            { PerfBias.SuperPi, "SuperPi" }
        };

        private static readonly Dictionary<PerfEnh, string> PerfEnhDict = new Dictionary<PerfEnh, string>
        {
            { PerfEnh.Auto, "Auto" },
            { PerfEnh.Default, "Default" },
            { PerfEnh.Level1, "Level 1" },
            { PerfEnh.Level2, "Level 2" },
            { PerfEnh.Level3_OC, "Level 3 (OC)" },
            { PerfEnh.Level4_OC, "Level 4 (OC)" }
        };

        private readonly Cpu cpu = new Cpu();
        private SystemInfo SI;
        private BackgroundWorker backgroundWorker;
        //private BindingSource siBindingSource;
        private PstateItem[] PstateItems;
        private int NUM_PSTATES = 3; // default set to 3, real active states are checked on a later stage

        private void HandleError(string message, string title = "Error")
        {
            MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void ExitApplication()
        {
            if (Application.MessageLoop)
                Application.Exit();
            else
                Environment.Exit(1);
        }

        private void SetStatus(string status)
        {
            statusText.Text = status;
        }

        private void RunBackgroundTask(DoWorkEventHandler task, RunWorkerCompletedEventHandler completedHandler)
        {
            try
            {
                backgroundWorker = new BackgroundWorker();
                backgroundWorker.DoWork += task;
                backgroundWorker.RunWorkerCompleted += completedHandler;
                backgroundWorker.RunWorkerAsync();
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message);
            }
        }

        private void InitSystemInfo(object sender, DoWorkEventArgs e)
        {
            if (cpu.info.family != Cpu.Family.FAMILY_17H && cpu.info.family != Cpu.Family.FAMILY_19H)
            {
                MessageBox.Show("CPU is not supported.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitApplication();
            }

            SI = new SystemInfo(cpu);
        }

        private void PopulateInfoTab()
        {
            /*siBindingSource = new BindingSource();
            siBindingSource.DataSource = si;
            cpuInfoLabel.DataBindings.Add("Text", siBindingSource, "CpuName", true);
            cpuIdLabel.DataBindings.Add("Text", siBindingSource, "CpuId", true);*/

            cpuInfoLabel.Text = SI.CpuName;
            mbVendorInfoLabel.Text = SI.MbVendor;
            mbModelInfoLabel.Text = SI.MbName;
            biosInfoLabel.Text = SI.BiosVersion;
            smuInfoLabel.Text = SI.GetSmuVersionString();
            cpuIdLabel.Text = $"{SI.GetCpuIdString()} ({cpu.info.codeName})";
            microcodeInfoLabel.Text = $"{SI.PatchLevel:X8}";
        }

        private void InitSystemInfo_Complete(object sender, RunWorkerCompletedEventArgs e)
        {
            // PopulatePstates();
            // InitManualOc();

            // Resize main form
            int pstatesHeight = groupBoxPstates.Height;
            int expectedPstateHeight = 115;

            if (pstatesHeight > expectedPstateHeight)
            {
                var formHeight = ActiveForm.Height;
                ActiveForm.Height = formHeight + pstatesHeight - expectedPstateHeight;
            }

            comboBoxPerfBias.DataSource = PerfBiasDict.ToList();
            comboBoxPerfBias.ValueMember = "Key";
            comboBoxPerfBias.DisplayMember = "Value";

            comboBoxPerfEnh.DataSource = PerfEnhDict.ToList();
            comboBoxPerfEnh.ValueMember = "Key";
            comboBoxPerfEnh.DisplayMember = "Value";

            PopulateInfoTab();
            SetStatus("Ready.");

            //Enabled = true;
        }

        private double GetCurrentMulti(bool ocmode = false)
        {
            uint msr = ocmode ? MSR_PSTATE_BOOST : MSR_PStateDef0;
            uint eax = default, edx = default;

            if (cpu.Ols.Rdmsr(msr, ref eax, ref edx) != 1)
            {
                HandleError("Error getting current multiplier!");
                return 0;
            }

            double multi = 25 * (eax & 0xFF) / (12.5 * (eax >> 8 & 0x3F));

            // OC mode only supports multipliers in 0.25 increment
            return Math.Round(multi * 4, MidpointRounding.ToEven) / 4;
        }

        private double GetCoreMulti(int index)
        {
            uint eax = default, edx = default;
            if (cpu.Ols.RdmsrTx(MSR_PSTATE_BOOST, ref eax, ref edx, (UIntPtr)(1 << index)) != 1)
            {
                //HandleError($"Error getting core{index} frequency");
                return 0;
            }

            double multi = 25 * (eax & 0xFF) / (12.5 * (eax >> 8 & 0x3F));
            return Math.Round(multi * 4, MidpointRounding.ToEven) / 4;
        }

        private byte GetCurrentVid(bool ocmode = false)
        {
            uint msr = ocmode ? MSR_PSTATE_BOOST : MSR_PStateDef0;
            uint eax = default, edx = default;

            if (cpu.Ols.Rdmsr(msr, ref eax, ref edx) != 1)
            {
                HandleError("Error getting current VID!");
                return 0;
            }

            return (byte)(eax >> 14 & 0xFF);
        }

        private bool GetCPB()
        {
            uint eax = 0, edx = 0;
            if (cpu.Ols.Rdmsr(MSR_HWCR, ref eax, ref edx) != 1)
            {
                HandleError("Error getting CPB MSR!");
                return false;
            }

            return cpu.utils.GetBits(eax, 25, 1) == 0;
        }

        private void SetCPB(bool en)
        {
            uint eax = 0, edx = 0;
            bool res = (cpu.Ols.Rdmsr(MSR_HWCR, ref eax, ref edx) == 1);

            if (res)
            {
                eax = cpu.utils.SetBits(eax, 25, 1, en ? 0 : 1U);
                res = cpu.WriteMsr(MSR_HWCR, eax, edx);
            }

            if (!res)
                HandleError("Error setting CPB!");
        }

        // Package C6-State
        private bool GetC6Package()
        {
            uint eax = 0, edx = 0;
            if (cpu.Ols.Rdmsr(MSR_PMGT_MISC, ref eax, ref edx) != 1)
            {
                HandleError("Error getting Package C6-State MSR!");
                return false;
            }

            return cpu.utils.GetBits(edx, 0, 1) == 1;
        }

        private void SetC6Package(bool en)
        {
            uint eax = 0, edx = 0;
            bool res = cpu.Ols.Rdmsr(MSR_PMGT_MISC, ref eax, ref edx) == 1;

            if (res)
            {
                uint val = en ? 1U : 0;

                edx = cpu.utils.SetBits(edx, 0, 1, val);
                res = cpu.WriteMsr(MSR_PMGT_MISC, eax, edx);
            }

            if (!res)
            {
                HandleError("Error setting Package C6-State!");
                return;
            }
        }

        // Core C6-State
        private bool GetC6Core()
        {
            uint eax = default, edx = default;
            if (cpu.Ols.Rdmsr(MSR_CSTATE_CONFIG, ref eax, ref edx) != 1)
            {
                HandleError("Error getting Core C6-State!");
                return false;
            }

            bool CCR0_CC6EN = Convert.ToBoolean(cpu.utils.GetBits(eax, 6, 1));
            bool CCR1_CC6EN = Convert.ToBoolean(cpu.utils.GetBits(eax, 14, 1));
            bool CCR2_CC6EN = Convert.ToBoolean(cpu.utils.GetBits(eax, 22, 1));

            return CCR0_CC6EN && CCR1_CC6EN && CCR2_CC6EN;
        }

        private void SetC6Core(bool en)
        {
            uint eax = 0, edx = 0;
            bool res = cpu.Ols.Rdmsr(MSR_CSTATE_CONFIG, ref eax, ref edx) == 1;

            if (res)
            {
                uint val = en ? 1U : 0;

                eax = cpu.utils.SetBits(eax, 6, 1, val);
                eax = cpu.utils.SetBits(eax, 14, 1, val);
                eax = cpu.utils.SetBits(eax, 22, 1, val);

                res = cpu.WriteMsr(MSR_CSTATE_CONFIG, eax, edx);
            }

            if (!res)
            {
                HandleError("Error setting Core C6-State!");
                return;
            }
        }


        #region UI Functions

        private void PopulatePstates()
        {
            uint eax = default, edx = default;

            cpu.Ols.Rdmsr(MSR_PStateCurLim, ref eax, ref edx);
            NUM_PSTATES = Convert.ToInt32((eax >> 4) & 0x7) + 1;
            PstateItems = new PstateItem[NUM_PSTATES];

            for (int i = 0; i < NUM_PSTATES; i++)
            {
                cpu.Ols.Rdmsr(MSR_PStateDef0 + Convert.ToUInt32(i), ref eax, ref edx);
                PstateItems[i] = new PstateItem
                {
                    Label = $"P{i}",
                    Pstate = (ulong)edx << 32 | eax
                };
                tableLayoutPanel3.Controls.Add(PstateItems[i], 0, i);
            }
        }

        private void RefreshState()
        {
            uint eax = default, edx = default;

            InitManualOc();

            for (int i = 0; i < NUM_PSTATES; i++)
            {
                cpu.Ols.Rdmsr(MSR_PStateDef0 + Convert.ToUInt32(i), ref eax, ref edx);
                PstateItems[i].Pstate = (ulong)edx << 32 | eax;
            }

            SetStatus("Refresh OK.");
        }

        private void InitManualOc()
        {
            bool ocmode = cpu.GetOcMode();
            manualOverclockItem.OCmode = ocmode;
            manualOverclockItem.Vid = GetCurrentVid(ocmode);
            manualOverclockItem.Multi = GetCurrentMulti(ocmode);
            manualOverclockItem.ProchotEnabled = cpu.IsProchotEnabled();
            manualOverclockItem.coreDisableMap = cpu.info.coreDisableMap;
            manualOverclockItem.CcxInCcd = cpu.info.family == Cpu.Family.FAMILY_19H ? 1 : 2;
            manualOverclockItem.Cores = (int)cpu.info.physicalCores; // SI.FusedCoreCount;
        }

        private void WaitForPowerTable()
        {
            Stopwatch timer = new Stopwatch();
            timer.Start();

            uint temp;
            // Refresh until table is transferred to DRAM or timeout
            do
                InteropMethods.GetPhysLong((UIntPtr)dramBaseAddress, out temp);
            while (temp == 0 && timer.Elapsed.TotalMilliseconds < 10000);

            timer.Stop();
        }

        private void InitPowerTab()
        {
            checkBoxCPB.Checked = GetCPB();
            checkBoxC6Core.Checked = GetC6Core();
            checkBoxC6Package.Checked = GetC6Package();

            numericUpDownScalar.Value = Convert.ToDecimal(cpu.GetPBOScalar());

            WaitForPowerTable();

            if (dramBaseAddress > 0)
            {
                try
                {
                    if (cpu.TransferTableToDram() != SMU.Status.OK)
                        cpu.TransferTableToDram(); // retry

                    UIntPtr dramPtr = new UIntPtr(dramBaseAddress);
                    string result = "";

                    InteropMethods.GetPhysLong(dramPtr, out uint data);
                    byte[] bytes = BitConverter.GetBytes(data);
                    result += $"PPT: {BitConverter.ToSingle(bytes, 0)}W" + Environment.NewLine;
                    numericUpDownPPT.Value = Convert.ToDecimal(BitConverter.ToSingle(bytes, 0));

                    /*for (int i = 0; i <= table_size; i += 4)
                    {
                        GetPhysLong(dramPtr + i, out uint value);
                        bytes = BitConverter.GetBytes(value);
                        Console.WriteLine($"offset {i:X4}: {BitConverter.ToSingle(bytes, 0)}");
                    }*/

                    InteropMethods.GetPhysLong(dramPtr + 0x008, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"TDC: {BitConverter.ToSingle(bytes, 0)}A" + Environment.NewLine;
                    numericUpDownTDC.Value = Convert.ToDecimal(BitConverter.ToSingle(bytes, 0));

                    InteropMethods.GetPhysLong(dramPtr + 0x010, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"EDC: {BitConverter.ToSingle(bytes, 0)}A" + Environment.NewLine;
                    numericUpDownEDC.Value = Convert.ToDecimal(BitConverter.ToSingle(bytes, 0));
                    /*
                    GetPhysLong(dramPtr + 0x010, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"TjMax: {BitConverter.ToSingle(bytes, 0)}" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x060, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"CorePower: {BitConverter.ToSingle(bytes, 0)}W" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x064, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"SOCPower: {BitConverter.ToSingle(bytes, 0)}W" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x0C0, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"FCLK: {BitConverter.ToSingle(bytes, 0)}MHz" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x0C4, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"SomeCLK: {BitConverter.ToSingle(bytes, 0)}MHz" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x0C8, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"UCLK: {BitConverter.ToSingle(bytes, 0)}MHz" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x0CC, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"MCLK: {BitConverter.ToSingle(bytes, 0)}MHz" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x108, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"BCLK: {BitConverter.ToSingle(bytes, 0)}MHz" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x0B4, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"Vsoc: {BitConverter.ToSingle(bytes, 0)}V" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x1F4, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"Vddp: {BitConverter.ToSingle(bytes, 0)}V" + Environment.NewLine;

                    GetPhysLong(dramPtr + 0x1F8, out data);
                    bytes = BitConverter.GetBytes(data);
                    result += $"Vddg: {BitConverter.ToSingle(bytes, 0)}V" + Environment.NewLine;
                    */
                    //Thread t = new Thread(() => MessageBox.Show(result));
                    //t.Start();
                }
                catch { }
            }
        }

        // P0 fix C001_0015 HWCR[21]=1
        // Fixes timer issues when not using HPET
        private bool ApplyTscWorkaround()
        {
            uint eax = 0, edx = 0;

            if (cpu.Ols.Rdmsr(MSR_HWCR, ref eax, ref edx) != -1)
            {
                eax |= 0x200000;
                return cpu.WriteMsr(MSR_HWCR, eax, edx);
            }
            return false;
        }

        private bool SetFrequencyAllCore(int frequency)
        {
            uint[] args = {Convert.ToUInt32(frequency), 0 };
            // TODO: Add Manual OC mode
            if (cpu.SendSmuCommand(cpu.smu.SMU_MSG_SetOverclockFrequencyAllCores, ref args) != SMU.Status.OK)
            {
                HandleError("Error setting frequency!");
                return false;
            }
            return true;
        }

        private bool SetFrequencySingleCore(int mask, int frequency)
        {
            uint[] args = { Convert.ToUInt32(mask | frequency & 0xFFFFF), 0 };
            if (cpu.SendSmuCommand(cpu.smu.SMU_MSG_SetOverclockFrequencyPerCore, ref args) != SMU.Status.OK)
            {
                HandleError("Error setting core frequency!");
                return false;
            }
            return true;
        }

        private bool SetFrequencyMultipleCores(uint mask, int frequency, int count)
        {
            // ((i.CCD << 4 | i.CCX % 2 & 0xF) << 4 | i.CORE % 4 & 0xF) << 20;
            for (uint i = 0; i <= count; i++)
            {
                mask = cpu.utils.SetBits(mask, 20, 2, i);
                if (!SetFrequencySingleCore((int)mask, frequency))
                    return false;
            }
            return true;
        }

        private bool SetFrequencyCCX(uint mask, int frequency)
        {
            bool ret = SetFrequencyMultipleCores(mask, frequency, SI.NumCoresInCCX);
            if (!ret)
                HandleError("Error setting CCX frequency!");
            return ret;
        }

        private bool SetFrequencyCCD(uint mask, int frequency)
        {
            bool ret = true;
            for (uint i = 0; i <= SI.CCXCount / SI.CCDCount; i++)
            {
                mask = cpu.utils.SetBits(mask, 24, 1, i);
                ret = SetFrequencyCCX(mask, frequency);
            }
            if (!ret)
                HandleError("Error setting CCD frequency!");
            return ret;
        }

        private bool SetOCVid(byte vid)
        {
            uint[] args = { Convert.ToUInt32(vid), 0 };
            if (cpu.SendSmuCommand(cpu.smu.SMU_MSG_SetOverclockCpuVid, ref args) != SMU.Status.OK)
            {
                HandleError("Error setting CPU Overclock VID!");
                return false;
            }
            return true;
        }

        private bool SetOcMode(bool enabled, uint arg = 0U)
        {
            uint cmd = enabled ? cpu.smu.SMU_MSG_EnableOcMode : cpu.smu.SMU_MSG_DisableOcMode;
            uint[] args = { arg };
            if (cpu.SendSmuCommand(cmd, ref args) != SMU.Status.OK)
            {
                HandleError("Error setting OC mode!");
                return false;
            }
            return true;
        }

        private bool SetProchot(bool enabled = true)
        {
            uint arg = enabled ? 0U : 0x1000000;
            bool res = SetOcMode(!enabled, arg);

            // Re-enable OC mode
            if (enabled) res = SetOcMode(true);

            return res;
        }

        private void ApplyManualOcSettings()
        {
            bool ret = false;
            var item = manualOverclockItem;
            int targetFreq = (int)(item.Multi * 100.00);
            int currentFreq = (int)(GetCurrentMulti(true) * 100.00);

            if (targetFreq > currentFreq)
                SetOCVid(item.Vid);

            if (item.AllCores)
            {
                ret = SetFrequencyAllCore(targetFreq);
            }
            else
            {
                switch (item.ControlMode)
                {
                    case 0:
                        if (SetFrequencyAllCore(550))
                            ret = SetFrequencySingleCore(item.CoreMask, targetFreq);
                        break;
                    case 1:
                        ret = SetFrequencyCCX((uint)item.CoreMask, targetFreq);
                        break;
                    case 2:
                        ret = SetFrequencyCCD((uint)item.CoreMask, targetFreq);
                        break;
                    default:
                        break;
                }
            }

            if (targetFreq <= currentFreq)
                SetOCVid(item.Vid);

            if (ret)
            {
                item.UpdateState();
                SetStatus("OK.");
            }
            else
            {
                item.Reset();
                SetStatus("Error.");
            }
        }

        private void ApplyPstates()
        {
            for (int i = 0; i < NUM_PSTATES; i++)
            {
                var item = PstateItems[i];
                if (item.Changed)
                {
                    if (!ApplyTscWorkaround()) return;

                    uint eax = Convert.ToUInt32(item.Pstate & 0xFFFFFFFF);
                    uint edx = Convert.ToUInt32(item.Pstate >> 32);

                    if (!cpu.WriteMsr(MSR_PStateDef0 + Convert.ToUInt32(i), eax, edx))
                    {
                        Console.WriteLine($"Could not update Pstate{i}");
                        item.Reset();
                        return;
                    }

                    item.UpdateState();
                }
            }
        }

        private bool ApplyPerfBias(PerfBias pb)
        {
            uint pb1_eax = 0, pb1_edx = 0, pb2_eax = 0, pb2_edx = 0, pb3_eax = 0, pb3_edx = 0, pb4_eax = 0, pb4_edx = 0, pb5_eax = 0, pb5_edx = 0;

            // Read current settings
            if (cpu.Ols.Rdmsr(MSR_PERFBIAS1, ref pb1_eax, ref pb1_edx) != 1) return false;
            if (cpu.Ols.Rdmsr(MSR_PERFBIAS2, ref pb2_eax, ref pb2_edx) != 1) return false;
            if (cpu.Ols.Rdmsr(MSR_PERFBIAS3, ref pb3_eax, ref pb3_edx) != 1) return false;
            if (cpu.Ols.Rdmsr(MSR_PERFBIAS4, ref pb4_eax, ref pb4_edx) != 1) return false;
            if (cpu.Ols.Rdmsr(MSR_PERFBIAS5, ref pb5_eax, ref pb5_edx) != 1) return false;

            // Clear by default
            pb1_eax &= 0xFFFFFFEF;
            pb2_eax &= 0xFF83FFFF;
            pb2_eax |= 0x02000000;
            pb3_eax &= 0xFFFFFFF8;
            pb4_eax &= 0xFFF9FFEF;
            pb5_eax &= 0xFFFFFFFE;

            // Specific settings
            switch (pb)
            {
                case PerfBias.None:
                    pb1_eax |= 0x10;
                    pb2_eax |= (8 & 0x1F) << 18;
                    pb3_eax |= (7 & 0x7);
                    pb4_eax |= 0x10;
                    break;
                case PerfBias.Cinebench_R11p5:
                    pb2_eax &= 0xF1FFFFEF;
                    pb3_eax |= (7 & 0x7);
                    pb4_eax |= 0x60010;
                    break;
                /*case PerfBias.Cinebench_R15:
                    pb2_eax |= (3 & 0x1F) << 18;
                    pb2_eax &= 0xF1FFFFEF;
                    pb3_eax |= (6 & 0x7);
                    pb4_eax |= 0x10;
                    pb5_eax |= 1;
                    break;*/
                case PerfBias.Cinebench_R15:
                    pb1_edx &= 0xFFF00F0F;
                    pb2_eax |= (3 & 0x1F) << 18;
                    pb2_eax |= 0x40;
                    pb2_eax &= 0xF1FFFFEF;
                    pb3_eax |= (7 & 0x7);
                    pb4_eax |= 0x15;
                    pb4_edx &= 0x0;
                    pb5_eax |= 1;
                    break;
                case PerfBias.Geekbench_3:
                    //pb2_eax &= 0xF1FFFFEF;
                    pb2_eax |= (4 & 0x1F) << 18;
                    pb3_eax |= (7 & 0x7);
                    pb4_eax |= 0x10;
                    break;
                case PerfBias.SuperPi:
                    //pb2_eax |= (1 & 0x1F) << 18;
                    pb3_eax |= (9 & 0x7);

                    break;
                case PerfBias.Auto:
                default:
                    return false;
            }

            // Rewrite
            if (!cpu.WriteMsr(MSR_PERFBIAS1, pb1_eax, pb1_edx)) return false;
            if (!cpu.WriteMsr(MSR_PERFBIAS2, pb2_eax, pb2_edx)) return false;
            if (!cpu.WriteMsr(MSR_PERFBIAS3, pb3_eax, pb3_edx)) return false;
            if (!cpu.WriteMsr(MSR_PERFBIAS4, pb4_eax, pb4_edx)) return false;
            if (!cpu.WriteMsr(MSR_PERFBIAS5, pb5_eax, pb5_edx)) return false;


            if (pb == PerfBias.SuperPi)
            {
                // Conditioner
                uint eax = 0; uint edx = 0;

                // NRAC; bit 7; 0 -> enabled, 1 -> disabled
                cpu.Ols.Rdmsr(0xC001102D, ref eax, ref edx);
                uint nrac = cpu.utils.GetBits(eax, 7, 1);

                MessageBox.Show(nrac == 1 ? "NRAC Disabled" : "NRAC Enabled");

                //if (nrac == 0)
                {
                    // Set it to Disabled (1)
                    eax = cpu.utils.SetBits(eax, 7, 1, 0x1);
                    cpu.WriteMsr(0xC001102D, eax, edx);
                }

                // TBM Activation; bit 22; 1 -> disabled, 0 -> enabled
                cpu.Ols.Rdmsr(0xC0011029, ref eax, ref edx);
                uint tbm = cpu.utils.GetBits(eax, 22, 1);
                MessageBox.Show(tbm == 1 ? "TBM Disabled" : "TBM Enabled");

                //if (tbm == 1)
                {
                    // MSRC001_1029 Decode Configuration (DE_CFG)
                    // Set it to Enabled (0)
                    eax = cpu.utils.SetBits(eax, 22, 1, 0x0);
                    cpu.WriteMsr(0xC0011029, eax, edx);

                    // MSRC001_1005 Extended CPUID Features (ExtFeatures)
                    // [53] TBM
                    cpu.Ols.Rdmsr(0xC0011005, ref eax, ref edx);
                    edx = cpu.utils.SetBits(edx, 21, 1, 0x1);
                    cpu.WriteMsr(0xC0011005, eax, edx);
                }

                // Stack; bit 15; 0 -> disabled, 1 -> enabled
                cpu.Ols.Rdmsr(0xC0011000, ref eax, ref edx);
                uint stack = cpu.utils.GetBits(eax, 15, 1);
                MessageBox.Show(stack == 0 ? "Stack Disabled" : "Stack Enabled");

                //if (stack == 0)
                {
                    // Set it to Enabled (1)
                    eax = cpu.utils.SetBits(eax, 15, 1, 0x1);
                    cpu.WriteMsr(0xC0011000, eax, edx);

                    // MSRC001_1021 Instruction Cache Configuration (IC_CFG)
                    // [4:1] DisIcWayFilter: disable IC way access filter - 0xF (disable)
                    // [39] DisLoopPredictor = 1
                    /*
                    cpu.Ols.Rdmsr(0xC0011021, ref eax, ref edx);
                    eax = cpu.utils.SetBits(eax, 1, 4, 0xF);
                    edx = cpu.utils.SetBits(edx, 7, 1, 0x1);
                    cpu.WriteMsr(0xC0011021, eax, edx);
                    */

                    // MSRC001_1023 Combined Unit Configuration (CU_CFG)
                    // [22:19] L2FirstLockedWay: first L2 way locked. - 0xF (Way 15 locked)
                    // [23] L2WayLock: L2 way lock enable
                    // [56] Reserved
                    cpu.Ols.Rdmsr(0xC0011023, ref eax, ref edx);
                    eax = cpu.utils.SetBits(eax, 19, 4, 0xF);
                    // ? eax = cpu.utils.SetBits(eax, 23, 1, 0x1);
                    edx = cpu.utils.SetBits(edx, 24, 1, 0x1);
                    cpu.WriteMsr(0xC0011023, eax, edx);

                    // MSRC001_1028 Floating Point Configuration (FP_CFG)
                    // [44:42] - DiDtCfg4
                    // [41] - DiDtCfg5
                    // [40] - DiDtCfg3
                    cpu.Ols.Rdmsr(0xC0011028, ref eax, ref edx);
                    edx = cpu.utils.SetBits(edx, 8, 5, 0x1A);
                    // ? edx = cpu.utils.SetBits(edx, 8, 8, 0xAA);
                    cpu.WriteMsr(0xC0011028, eax, edx);

                    // MSRC001_1029 Decode Configuration (DE_CFG)
                    // [10] ResyncPredSingleDispDis = 0
                    /*cpu.Ols.Rdmsr(0xC0011029, ref eax, ref edx);
                    eax &= 0xFBFF;
                    cpu.WriteMsr(0xC0011029, eax, edx);*/

                    // MSRC001_102B Combined Unit Configuration 3 (CU_CFG3)
                    // [20:21] PfcStrideMul = 01b 4
                    // [22] PfcDoubleStride = 1
                    // [41:23] Reserved
                    cpu.Ols.Rdmsr(0xC001102B, ref eax, ref edx);
                    eax = cpu.utils.SetBits(eax, 20, 8, 0x55);
                    edx &= 0xFBFF;
                    cpu.WriteMsr(0xC001102B, eax, edx);
                }
            }

            return true;
        }
        #endregion

        public AppWindow()
        {
            InitializeComponent();

            //siBindingSource = new BindingSource();
            ToolTip toolTip = new ToolTip();

            //Enabled = false;

            trayMenuItemApp.Text = Application.ProductName + " " + Application.ProductVersion.Substring(0, Application.ProductVersion.LastIndexOf('.'));

            try
            {
                if (cpu.info.codeName == Cpu.CodeName.Unsupported)
                {
                    HandleError("CPU is not supported");
                    ExitApplication();
                }

                SetStatus("Initializing...");

                if (cpu.smu.Version == 0)
                {
                    HandleError("Error getting SMU version!\n" +
                        "Default SMU addresses are not responding to commands.");
                }

                dramBaseAddress = (uint)(cpu.GetDramBaseAddress() & 0xFFFFFFFF);

                PopulatePstates();
                InitPowerTab();
                InitManualOc();
                RunBackgroundTask(InitSystemInfo, InitSystemInfo_Complete);

                //Enabled = true;
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message);
                Dispose();
                Application.Exit();
            }
        }

        private void ButtonApply_Click(object sender, EventArgs e)
        {
            var selectedTab = tabControl1.SelectedTab;

            if (selectedTab == cpuTabOC)
            {
                if (manualOverclockItem.ModeChanged)
                {
                    if (SetOcMode(manualOverclockItem.OCmode))
                    {
                        manualOverclockItem.UpdateState();
                    }
                    else
                    {
                        manualOverclockItem.Reset();
                        return;
                    }
                }

                if (manualOverclockItem.OCmode)
                    ApplyManualOcSettings();
                else
                    ApplyPstates();
            }

            if (selectedTab == tabTweaks)
                ApplyPerfBias((PerfBias)comboBoxPerfBias.SelectedIndex);

            if (selectedTab == tabPower)
            {
                SetCPB(checkBoxCPB.Checked);
                SetC6Core(checkBoxC6Core.Checked);
                SetC6Package(checkBoxC6Package.Checked);
            }
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                trayIcon.Visible = true;
            }
        }

        private void NotifyIcon_DoubleClick(object sender, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
            trayIcon.Visible = false;
        }

        private void TrayMenuItemExit_Click(object sender, EventArgs e)
        {
            Dispose();
            Application.Exit();
        }

        static void MinimizeFootprint()
        {
            InteropMethods.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }

        // Get rid of flicker on form when painting child user controls
        int originalExStyle = -1;
        bool enableFormLevelDoubleBuffering = false;

        protected override CreateParams CreateParams
        {
            get
            {
                if (originalExStyle == -1)
                    originalExStyle = base.CreateParams.ExStyle;

                CreateParams cp = base.CreateParams;
                if (enableFormLevelDoubleBuffering)
                    cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
                else
                    cp.ExStyle = originalExStyle;

                return cp;
            }
        }

        private void TurnOffFormLevelDoubleBuffering()
        {
            enableFormLevelDoubleBuffering = false;
            MinimizeBox = true;
        }

        private void AppWindow_Shown(object sender, EventArgs e)
        {
            //Show();
            MinimizeFootprint();
        }

        private void ButtonRefresh_Click(object sender, EventArgs e)
        {
            RefreshState();
        }

        private void ManualOverclockItem_SlowModeClicked(object sender, EventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            int cores = SI.Threads;
            int step = SI.SMT ? SI.NumCoresInCCX * 2 : SI.NumCoresInCCX;
            int index = 0;

            double[] ccx_frequencies = new double[SI.CCXCount];

            if (cb.Checked)
            {
                for (var i = 0; i < cores; i += step)
                {
                    ccx_frequencies[index] = GetCoreMulti(i);
                    ++index;
                }
                Storage.Add($"ccx_frequencies", ccx_frequencies);
                Storage.Add("oc_vid", manualOverclockItem.Vid);

                for (var i = 0; i < SI.CCXCount; ++i)
                {
                    Console.WriteLine($"ccx{i}: " + Storage.Get<double[]>($"ccx_frequencies")[i].ToString());
                }

                SetFrequencyAllCore(550);
                SetOCVid(0x98);
            }
            else
            {
                // Single core mode
                if (manualOverclockItem.ControlMode == 0 && !manualOverclockItem.AllCores)
                {
                    ApplyManualOcSettings();
                }
                else
                {
                    int[] masks = new int[SI.CCXCount];

                    for (var i = 0; i < SI.PhysicalCoreCount; i += 4)
                    {
                        int ccd = i / 8;
                        int ccx = i / 4 - 2 * ccd;
                        masks[index] = (ccd << 4 | ccx) << 24;
                        ++index;
                    }

                    SetOCVid(Storage.Get<byte>($"oc_vid"));

                    for (var i = 0; i < SI.CCXCount; ++i)
                    {
                        int targetFreq = (int)(Storage.Get<double[]>($"ccx_frequencies")[i] * 100.00);
                        SetFrequencyCCX((uint)masks[i], targetFreq);
                    }
                    //RestoreManualOcSettings();
                }
            }
        }

        private void ManualOverclockItem_ProchotClicked(object sender, EventArgs e)
        {
            CheckBox cb = sender as CheckBox;
            bool res = SetProchot(cb.Checked);
            if (res) SetStatus("PROCHOT " + (cb.Checked ? "enabled." : "disabled."));
        }
    }
}
