using System;
using System.Windows;

namespace SunSync
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 仅在此版本中使用，它将
        /// 删除旧有的sync_jobs.db（如果有）和sync_jobs_v1.6.0.4.db（如果有）
        /// 重命名sync_jobs_v1.6.0.5.db（如果有）改为sync_jobs_v1.db
        /// </summary>
        /// <param name="e"></param>
        protected override void OnStartup(StartupEventArgs e)
        {
            #region CLEAN-UP-AND-INIT

            string myDocPath = System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            string jobsDb_0 = System.IO.Path.Combine(myDocPath, "qsunsync", "sync_jobs.db");
            string jobsDb_1 = System.IO.Path.Combine(myDocPath, "qsunsync", "sync_jobs_v1.6.0.4.db");
            if (System.IO.File.Exists(jobsDb_0))
            {
                System.IO.File.Delete(jobsDb_0);
            }
            if (System.IO.File.Exists(jobsDb_1))
            {
                System.IO.File.Delete(jobsDb_1);
            }

            string jobsDb_Old = System.IO.Path.Combine(myDocPath, "qsunsync", "sync_jobs_v1.6.0.5.db");
            string jobsDb_New = System.IO.Path.Combine(myDocPath, "qsunsync", "sync_jobs_v1.db");
            if (!System.IO.File.Exists(jobsDb_New)&& System.IO.File.Exists(jobsDb_Old))
            {
                System.IO.File.Move(jobsDb_Old, jobsDb_New);
            }

            #endregion CLEAN-UP-AND-INIT

            base.OnStartup(e);
        }

    }
}
