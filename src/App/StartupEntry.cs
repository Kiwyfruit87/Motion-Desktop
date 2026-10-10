using System;
using System.IO;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace VideoWallpaper
{
    // 開機自動啟動：用「工作排程器」在登入的當下直接啟動。
    // 以前用登錄檔的 Run 清單，但 Explorer 開機時會一個一個、等系統有空才啟動那份清單，常常要等好幾分鐘。
    // 查工作排程器要連線到系統服務（UI 執行緒上要等好幾毫秒），控制面板開著時每秒都會問，
    // 所以記住結果：打開面板時（fresh）、切換開關之後才重新查
    static class StartupEntry
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "VideoWallpaper";

        static bool? cache;
        public static bool IsEnabled(bool fresh = false)
        {
            if (fresh || cache == null) cache = StartupTaskExists() || RunEntryExists();
            return cache.Value;
        }

        static bool StartupTaskExists()
        {
            try { TaskFolder().GetTask(RunName); return true; }
            catch { return false; }
        }

        static bool RunEntryExists()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunName) != null;
        }

        public static void Set(bool enable)
        {
            bool taskCreated = false;
            try
            {
                dynamic folder = TaskFolder();
                if (enable)
                {
                    folder.RegisterTask(RunName, TaskXml(), 6, null, null, 3);   // 6 = 建立或更新；3 = 以登入的使用者身分執行
                    taskCreated = true;
                }
                else if (StartupTaskExists()) folder.DeleteTask(RunName, 0);
            }
            catch { }

            // 排程建立成功就移除舊的 Run 設定；萬一建立失敗，才退回用 Run
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable && !taskCreated) key.SetValue(RunName, "\"" + WinForms.Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
            cache = null;   // 下次問的時候重新查
        }

        // 已經開啟自動啟動時：舊的 Run 設定換成排程；exe 被搬走時更新路徑；
        // 排程被別的程式停用了（例如防毒軟體檢查新版 exe 時）就重新啟用，不然開機不會自動播放
        public static void Refresh()
        {
            if (!IsEnabled()) return;
            bool upToDate = false;
            try
            {
                dynamic task = TaskFolder().GetTask(RunName);
                string xml = task.Xml;
                bool enabled = task.Enabled;
                upToDate = enabled && !RunEntryExists() && xml.Contains(System.Security.SecurityElement.Escape(WinForms.Application.ExecutablePath));
            }
            catch { }
            if (!upToDate) Set(true);
        }

        static dynamic TaskFolder()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            return service.GetFolder("\\");
        }

        static string TaskXml()
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            string exe = System.Security.SecurityElement.Escape(WinForms.Application.ExecutablePath);
            string dir = System.Security.SecurityElement.Escape(Path.GetDirectoryName(WinForms.Application.ExecutablePath));
            return
@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>" + System.Security.SecurityElement.Escape(Lang.T("登入 Windows 後啟動動態桌布", "Starts Motion Desktop when you sign in to Windows")) + @"</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>" + user + @"</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author""><UserId>" + user + @"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author"">
    <Exec><Command>""" + exe + @"""</Command><WorkingDirectory>" + dir + @"</WorkingDirectory></Exec>
  </Actions>
</Task>";
        }
    }
}
