﻿using SunSync.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Data.SQLite;

namespace SunSync
{
    /// <summary>
    /// Interaction logic for SyncProgressPage.xaml
    /// </summary>
    public partial class SyncProgressPage : Page
    {
        private SyncSetting syncSetting;
        private MainWindow mainWindow;

        private string jobId;
        private string jobLogDir;
        private DateTime jobStart;

        private object progressLock;
        private object uploadLogLock;

        private UploadInfo[] uploadInfos;
        private object uploadInfoLock;
        //key is fileKey
        //value is datetime in milliseconds +":"+uploadedBytes
        private Dictionary<string, string> uploadedBytes;

        private object fileSkippedLock;
        private object fileExistsLock;
        private object fileOverwriteLock;
        private object fileNotOverwriteLock;
        private object fileUploadErrorLock;
        private object fileUploadSuccessLock;

        private int fileSkippedCount;
        private int fileExistsCount;
        private int fileOverwriteCount;
        private int fileNotOverwriteCount;
        private int fileUploadErrorCount;
        private int fileUploadSuccessCount;

        private StreamWriter fileSkippedWriter;
        private StreamWriter fileExistsWriter;
        private StreamWriter fileOverwriteWriter;
        private StreamWriter fileNotOverwriteWriter;
        private StreamWriter fileUploadErrorWriter;
        private StreamWriter fileUploadSuccessWriter;

        private string fileSkippedLogPath;
        private string fileExistsLogPath;
        private string fileOverwriteLogPath;
        private string fileNotOverwriteLogPath;
        private string fileUploadSuccessLogPath;
        private string fileUploadErrorLogPath;

        private bool cancelSignal;
        private bool finishSignal;

        private int doneCount;
        private int totalCount;

        private string localHashDBPath;
        private SQLiteConnection localHashDB;

        private string jobsDbPath;

        private List<FileItem> batchOpFiles;
        private Dictionary<string,bool> overwriteDict;

        private string cacheDir;
        private string cacheFilePathDone;
        private string cacheFilePathTemp;

        private string syncLogDir;
        private string syncLogDBPath;
        private SQLiteConnection syncLogDB;

        private Qiniu.IO.Model.UploadController upctl;

        public SyncProgressPage(MainWindow mainWindow)
        {
            InitializeComponent();
            this.mainWindow = mainWindow;
            this.batchOpFiles = new List<FileItem>();
            this.uploadedBytes = new Dictionary<string, string>();
            this.resetSyncStatus();

            upctl = new Qiniu.IO.Model.UploadController(uploadControl);

            //HaltActionButton.IsEnabled = false;
        }

        public SQLiteConnection SyncLogDB()
        {
            return this.syncLogDB;
        }

        public SQLiteConnection LocalHashDB()
        {
            return this.localHashDB;
        }

        //this is called before page loaded
        internal void LoadSyncSetting(SyncSetting syncSetting)
        {
            this.syncSetting = syncSetting;

            string jobName = string.Join("\t", new string[] { syncSetting.LocalDirectory, syncSetting.TargetBucket });
            this.jobId = Qiniu.Util.Hashing.CalcMD5X(jobName);

            string myDocPath = System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            this.jobsDbPath = System.IO.Path.Combine(myDocPath, "qsunsync", "sync_jobs_v1.db");
            this.jobLogDir = System.IO.Path.Combine(myDocPath, "qsunsync", "logs", jobId);

            string localHashDBDir = System.IO.Path.Combine(myDocPath, "qsunsync", "hashdb");
            if(!Directory.Exists(localHashDBDir))
            {
                Directory.CreateDirectory(localHashDBDir);
            }
            string bucketId = Qiniu.Util.Hashing.CalcMD5X(syncSetting.TargetBucket);
            this.localHashDBPath = System.IO.Path.Combine(localHashDBDir, bucketId + ".db");
            this.cacheDir = System.IO.Path.Combine(myDocPath, "qsunsync", "dircache");
            this.syncLogDir = System.IO.Path.Combine(myDocPath, "qsunsync", "synclog");
            this.syncLogDBPath = System.IO.Path.Combine(this.syncLogDir, jobId + ".log.db");

            string cacheId = jobId;
            this.cacheFilePathDone = System.IO.Path.Combine(cacheDir, cacheId + ".done");
            this.cacheFilePathTemp = System.IO.Path.Combine(cacheDir, cacheId + ".temp");
        }

        private void SyncProgressPageLoaded_EventHandler(object sender, RoutedEventArgs e)
        {
            //clear old sync status
            this.resetSyncStatus();

            //run new sync job
            Thread jobThread = new Thread(new ParameterizedThreadStart(this.runSyncJob));
            jobThread.Start(false);
        }

        private void resetSyncStatus()
        {
            this.doneCount = 0;
            this.totalCount = 0;
            this.progressLock = new object();
            this.uploadLogLock = new object();
            this.fileSkippedCount = 0;
            this.fileSkippedLock = new object();
            this.fileExistsCount = 0;
            this.fileExistsLock = new object();
            this.fileOverwriteCount = 0;
            this.fileOverwriteLock = new object();
            this.fileNotOverwriteCount = 0;
            this.fileNotOverwriteLock = new object();
            this.fileUploadErrorCount = 0;
            this.fileUploadErrorLock = new object();
            this.fileUploadSuccessCount = 0;
            this.fileUploadSuccessLock = new object();
            this.uploadInfoLock = new object();
            this.cancelSignal = false;
            this.finishSignal = false;
            this.HaltActionButton.Content = "暂停";
            //this.HaltActionButton.IsEnabled = true;
            this.ManualFinishButton.IsEnabled = false;
            this.UploadProgressTextBlock.Text = "";
            this.UploadProgressLogTextBlock.Text = "";
            ObservableCollection<UploadInfo> dataSource = new ObservableCollection<UploadInfo>();
            this.UploadProgressDataGrid.DataContext = dataSource;
            this.batchOpFiles.Clear();
        }

        private void createDirCache(string localSyncDir)
        {
            DirectoryInfo di = new DirectoryInfo(localSyncDir);

            FileInfo[] fileInfos = di.GetFiles("*.*", SearchOption.AllDirectories);

            int originalCount = fileInfos.Length;

            List<string> files = new List<string>();
            string[] keys = new string[originalCount];
            bool[] skip = new bool[originalCount];
            overwriteDict = new Dictionary<string, bool>();

            for(int i=0;i<originalCount;++i)
            {
                string fullName = fileInfos[i].FullName.Replace('\\', '/');
                string relativeName = fullName.Substring(syncSetting.LocalDirectory.Length + 1);
                string shortName = Path.GetFileName(fileInfos[i].Name);

                files.Add(fullName);                

                switch(syncSetting.FilenameKind)
                {
                    case (int)FilenameKind.FullPath:
                        keys[i] = syncSetting.SyncPrefix + fullName;
                        break;
                    case (int)FilenameKind.Relative:
                        keys[i] = syncSetting.SyncPrefix + relativeName;
                        break;
                    case (int)FilenameKind.ShortName:
                    default:
                        keys[i] = syncSetting.SyncPrefix + shortName;
                        break;
                }

                skip[i] = false;
                overwriteDict.Add(fullName, false);
            }

            // 按照前缀/后缀跳过
            string[] skippedPrefixes = this.syncSetting.SkipPrefixes.Split(',');
            string[] skippedSuffixes = this.syncSetting.SkipSuffixes.Split(',');
            for (int i = 0; i < originalCount; ++i)
            {
                string shortFileName = Path.GetFileName(fileInfos[i].Name);

                if (skip[i]) continue;
                if (shouldSkipByPrefix(shortFileName, skippedPrefixes))
                {
                    skip[i] = true;
                    addFileSkippedLog(string.Format("{0}\t{1}", this.syncSetting.TargetBucket,files[i]));
                    updateUploadLog("按照前缀规则跳过文件 " + files[i]);
                }

                if (skip[i]) continue;
                if (shouldSkipBySuffix(shortFileName, skippedSuffixes))
                {
                    skip[i] = true;
                    addFileSkippedLog(string.Format("{0}\t{1}", this.syncSetting.TargetBucket, files[i]));
                    updateUploadLog("按照后缀规则跳过文件 " + files[i]);
                }
            }         

            // 检查本地增量文件
            if (syncSetting.CheckNewFiles)
            {
                List<string> cachedKeys = CachedHash.GetAllKeys(localHashDB);

                Dictionary<string, string> itemDict = CachedHash.GetAllItems(localHashDB);

                for (int i = 0; i < originalCount; ++i)
                {
                    if (skip[i]) continue;

                    string f = files[i];
                    if (itemDict.ContainsKey(f))
                    {
                        string oldHash = itemDict[f];
                        string newHash = Qiniu.Util.ETag.CalcHash(f);
                        if (string.Equals(oldHash, newHash))
                        {
                            addFileExistsLog(string.Format("{0}\t{1}\t{2}", this.syncSetting.TargetBucket, files[i], keys[i]));
                            updateUploadLog("本地检查已同步，跳过文件 " + files[i]);
                            skip[i] = true;
                        }
                    }
                }
            }

            // 检查云端同名文件  
            var mac = new Qiniu.Util.Mac(SystemConfig.ACCESS_KEY, SystemConfig.SECRET_KEY);
            string[] remoteHash = BucketFileHash.BatchStat(mac, syncSetting.TargetBucket, keys);

            // 覆盖
            if (syncSetting.OverwriteDuplicate)
            {
                for (int i = 0; i < originalCount; ++i)
                {
                    if (skip[i]) continue;

                    if (!string.IsNullOrEmpty(remoteHash[i]))
                    {
                        string localHash = Qiniu.Util.ETag.CalcHash(files[i]);
                        if (string.Equals(localHash, remoteHash[i]))
                        {
                            addFileOverwriteLog(string.Format("{0}\t{1}\t{2}", this.syncSetting.TargetBucket, files[i], keys[i]));
                            updateUploadLog("空间已存在相同文件，覆盖文件 " + files[i]);
                            overwriteDict[files[i]] = true;
                        }
                    }
                }
            }
            else // 跳过
            {
                for (int i = 0; i < originalCount; ++i)
                {
                    if (skip[i]) continue;

                    if (!string.IsNullOrEmpty(remoteHash[i]))
                    {
                        string localHash = Qiniu.Util.ETag.CalcHash(files[i]);
                        if (string.Equals(localHash, remoteHash[i]))
                        {
                            addFileNotOverwriteLog(string.Format("{0}\t{1}\t{2}", this.syncSetting.TargetBucket, files[i], keys[i]));
                            updateUploadLog("空间已存在相同文件，跳过文件 " + files[i]);
                            skip[i] = true;
                        }
                    }
                } 
            }

            using (StreamWriter sw = new StreamWriter(cacheFilePathDone))
            {
                for (int i = 0; i < files.Count; ++i)
                {
                    if (!skip[i])
                    {
                        sw.WriteLine(files[i]);                        
                    }
                }
            }
        }

        private bool shouldSkipByPrefix(string shortFileName, string[] skippedPrefixes)
        {
            foreach (string prefix in skippedPrefixes)
            {
                string sks = prefix.Trim();
                if (!string.IsNullOrEmpty(sks))
                {
                    if (shortFileName.StartsWith(sks))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool shouldSkipBySuffix(string shortFileName, string[] skippedSuffixes)
        {
            foreach (string suffix in skippedSuffixes)
            {
                string sfx = suffix.Trim();
                if (!string.IsNullOrEmpty(sfx))
                {
                    if (shortFileName.EndsWith(suffix.Trim()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void processUpload(string cacheFilePath)
        {
            try
            {
                string filePath = null;

                //count
                using (StreamReader sr = new StreamReader(this.cacheFilePathDone, Encoding.UTF8))
                {
                    while ((filePath = sr.ReadLine()) != null)
                    {
                        this.totalCount += 1;
                    }
                }

                //upload
                using (StreamReader sr = new StreamReader(this.cacheFilePathDone, Encoding.UTF8))
                {
                    while ((filePath = sr.ReadLine()) != null)
                    {
                        if (this.finishSignal)
                        {
                            return;
                        }

                        if (this.batchOpFiles.Count < this.syncSetting.SyncThreadCount)
                        {
                            FileInfo fi = new FileInfo(filePath);

                            string fullName = fi.FullName.Replace('\\', '/');
                            string relativeName = fullName.Substring(syncSetting.LocalDirectory.Length + 1);
                            string shortName = Path.GetFileName(fi.Name);

                            string fn = "";

                            switch (syncSetting.FilenameKind)
                            {
                                case (int)FilenameKind.FullPath:
                                    fn = syncSetting.SyncPrefix + fullName;
                                    break;
                                case (int)FilenameKind.Relative:
                                    fn = syncSetting.SyncPrefix + relativeName;
                                    break;
                                case (int)FilenameKind.ShortName:
                                default:
                                    fn = syncSetting.SyncPrefix + shortName;
                                    break;
                            }

                            FileItem item = new FileItem()
                            {
                                LocalFile = filePath,
                                SaveKey = fn,
                                FileHash = Qiniu.Util.ETag.CalcHash(filePath),
                                Length = fi.Length,
                                LastUpdate = fi.LastWriteTime.Ticks.ToString(),
                                Uploaded = false
                            };
                            this.batchOpFiles.Add(item);
                        }
                        else
                        {
                            this.uploadFiles(this.batchOpFiles);
                            this.batchOpFiles.Clear();
                            FileInfo fi = new FileInfo(filePath);
                            string fullName = fi.FullName.Replace('\\', '/');
                            string relativeName = fullName.Substring(syncSetting.LocalDirectory.Length + 1);
                            string shortName = Path.GetFileName(fi.Name);

                            string fn = "";

                            switch (syncSetting.FilenameKind)
                            {
                                case (int)FilenameKind.FullPath:
                                    fn = syncSetting.SyncPrefix + fullName;
                                    break;
                                case (int)FilenameKind.Relative:
                                    fn = syncSetting.SyncPrefix + relativeName;
                                    break;
                                case (int)FilenameKind.ShortName:
                                default:
                                    fn = syncSetting.SyncPrefix + shortName;
                                    break;
                            }

                            FileItem item = new FileItem()
                            {
                                LocalFile = filePath,
                                SaveKey = fn,
                                FileHash = Qiniu.Util.ETag.CalcHash(filePath),
                                Length = fi.Length,
                                LastUpdate = fi.LastWriteTime.Ticks.ToString(),
                                Uploaded = false
                            };

                            this.batchOpFiles.Add(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(string.Format("open cache file {0} failed due to {1}", this.cacheFilePathDone, ex.Message));
            }
        }

        private void uploadFiles(List<FileItem> filesToUpload)
        {
            this.uploadedBytes.Clear();
            ManualResetEvent[] doneEvents = null;
            int taskMax = filesToUpload.Count;
            doneEvents = new ManualResetEvent[taskMax];
            this.uploadInfos = new UploadInfo[taskMax];
            for (int taskId = 0; taskId < taskMax; taskId++)
            {
                this.uploadInfos[taskId] = new UploadInfo();
                doneEvents[taskId] = new ManualResetEvent(false);
                FileUploader uploader = new FileUploader(this.syncSetting, doneEvents[taskId], this, taskId, upctl);
                ThreadPool.QueueUserWorkItem(new WaitCallback(uploader.uploadFile), filesToUpload[taskId]);
            }

            try
            {
                WaitHandle.WaitAll(doneEvents);
                List<FileItem> filesUploaded = new List<FileItem>();
                foreach(var f in filesToUpload)
                {
                    if(f.Uploaded)
                    {
                        filesUploaded.Add(f);
                    }
                }
                CachedHash.BatchInsertOrUpdate(filesUploaded, localHashDB);
                SyncLog.BatchInsertOrUpdate(filesUploaded, syncLogDB);
            }
            catch (Exception ex)
            {
                Log.Error("wait for job to complete error, " + ex.Message);
            }
        }

        private bool initRunJob()
        {
            bool checkOk = true;
            if (!Directory.Exists(this.jobLogDir))
            {
                try
                {
                    Directory.CreateDirectory(this.jobLogDir);
                }
                catch (Exception ex)
                {
                    checkOk = false;
                    Log.Error(string.Format("create job log dir {0} failed due to {1},", this.jobLogDir, ex.Message));
                }
            }

            if (!Directory.Exists(this.cacheDir))
            {
                try
                {
                    Directory.CreateDirectory(this.cacheDir);
                }
                catch (Exception ex)
                {
                    checkOk = false;
                    Log.Error(string.Format("create list cache dir {0} failed due to {1},", this.cacheDir, ex.Message));
                }
            }

            if (!Directory.Exists(this.syncLogDir))
            {
                try
                {
                    Directory.CreateDirectory(this.syncLogDir);
                }
                catch (Exception ex)
                {
                    checkOk = false;
                    Log.Error(string.Format("create sync log dir {0} failed due to {1},", this.syncLogDir, ex.Message));
                }
            }

            //create the upload log files
            try
            {
                this.fileSkippedLogPath = System.IO.Path.Combine(this.jobLogDir, "skipped.log");
                this.fileExistsLogPath = System.IO.Path.Combine(this.jobLogDir, "exists.log");
                this.fileOverwriteLogPath = System.IO.Path.Combine(this.jobLogDir, "overwrite.log");
                this.fileNotOverwriteLogPath = System.IO.Path.Combine(this.jobLogDir, "not_overwrite.log");
                this.fileUploadSuccessLogPath = System.IO.Path.Combine(this.jobLogDir, "success.log");
                this.fileUploadErrorLogPath = System.IO.Path.Combine(this.jobLogDir, "error.log");

                this.fileSkippedWriter = new StreamWriter(fileSkippedLogPath, false, Encoding.UTF8);
                this.fileExistsWriter = new StreamWriter(fileExistsLogPath, false, Encoding.UTF8);
                this.fileOverwriteWriter = new StreamWriter(fileOverwriteLogPath, false, Encoding.UTF8);
                this.fileNotOverwriteWriter = new StreamWriter(fileNotOverwriteLogPath, false, Encoding.UTF8);
                this.fileUploadSuccessWriter = new StreamWriter(fileUploadSuccessLogPath, false, Encoding.UTF8);
                this.fileUploadErrorWriter = new StreamWriter(fileUploadErrorLogPath, false, Encoding.UTF8);

                this.fileSkippedWriter.AutoFlush = true;
                this.fileExistsWriter.AutoFlush = true;
                this.fileOverwriteWriter.AutoFlush = true;
                this.fileNotOverwriteWriter.AutoFlush = true;
                this.fileUploadSuccessWriter.AutoFlush = true;
                this.fileUploadErrorWriter.AutoFlush = true;
            }
            catch (Exception ex)
            {
                checkOk = false;
                Log.Error(string.Format("init the log writer for job {0} failed due to {1} ", this.jobId, ex.Message));
            }

            return checkOk;
        }

        private Qiniu.IO.Model.UPTS uploadControl()
        {
            if (finishSignal)
            {
                return Qiniu.IO.Model.UPTS.Aborted;
            }
            else
            {
                if (cancelSignal)
                {
                    return Qiniu.IO.Model.UPTS.Suspended;
                }
                else
                {
                    return Qiniu.IO.Model.UPTS.Activated;
                }
            }
        }

        private void createOptionalDB()
        {
            //check jobs db
            if (!File.Exists(this.jobsDbPath))
            {
                try
                {
                    SyncRecord.CreateSyncRecordDB(this.jobsDbPath);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("create sync record db for job {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
            else
            {
                DateTime syncDateTime = DateTime.Now;
                try
                {
                    SyncRecord.InsertRecord(this.jobId, syncDateTime, this.syncSetting, this.jobsDbPath);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("record sync job failed for job {0} due to {1}", this.jobId, ex.Message));
                }
            }

            if (!File.Exists(this.localHashDBPath))
            {
                try
                {
                    CachedHash.CreateCachedHashDB(this.localHashDBPath);
                }
                catch (Exception ex)
                {
                    Log.Error("create cached hash db failed, " + ex.Message);
                }
            }

            if (!File.Exists(this.syncLogDBPath))
            {
                try
                {
                    SyncLog.CreateSyncLogDB(this.syncLogDBPath);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("create sync log db for job {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }

        public bool isOverwritten(string localFile)
        {
            return overwriteDict[localFile];
        }

        //main job scheduler
        private void runSyncJob(object resumeObject)
        {
            //Dispatcher.Invoke(new Action(delegate ()
            //{
            //    HaltActionButton.IsEnabled = false;
            //}));

            bool resume = (bool)resumeObject;
            this.jobStart = DateTime.Now;
            bool checkOk = this.initRunJob();
            if (!checkOk)
            {
                this.updateUploadLog("同步发生严重错误，请查看日志信息。");
                //Dispatcher.Invoke(new Action(delegate
                //{
                //    this.ManualFinishButton.IsEnabled = true;
                //    this.HaltActionButton.IsEnabled = false;
                //}));
                return;
            }
            //create optional db
            this.createOptionalDB();
            try
            {
                //open database local hash db
                string conStr = new SQLiteConnectionStringBuilder { DataSource = this.localHashDBPath }.ToString();
                this.localHashDB = new SQLiteConnection(conStr);
                this.localHashDB.Open();
            }
            catch (Exception ex)
            {
                Log.Error(string.Format("open local hash db failed due to {0}", ex.Message));
                if (this.localHashDB != null)
                {
                    try
                    {
                        this.localHashDB.Close();
                    }
                    catch (Exception ex2)
                    {
                        Log.Error(string.Format("close local hash db failed due to {0}", ex2.Message));
                    }
                }
                this.localHashDB = null;
            }

            try
            {
                string conStr = new SQLiteConnectionStringBuilder { DataSource = this.syncLogDBPath }.ToString();
                this.syncLogDB = new SQLiteConnection(conStr);
                this.syncLogDB.Open();
            }
            catch (Exception ex)
            {
                Log.Error(string.Format("open sync log db failed due to {0}", ex.Message));
                if (this.syncLogDB != null)
                {
                    try
                    {
                        this.syncLogDB.Close();
                    }
                    catch (Exception ex2)
                    {
                        Log.Error(string.Format("close sync log db failed due to {0}", ex2.Message));
                    }
                }
                this.syncLogDB = null;
            }

            //start job
            this.jobStart = System.DateTime.Now;
            Log.Info(string.Format("start to sync dir {0}", this.syncSetting.LocalDirectory));
            //set before run status
            this.finishSignal = false;
            this.cancelSignal = false;

            //list dirs
            string localSyncDir = syncSetting.LocalDirectory;
            //list & count
            //if (!File.Exists(this.cacheFilePathDone) || (!resume))
            //{
                this.updateUploadLog(string.Format("正在遍历{0}下文件...", localSyncDir));
                this.createDirCache(localSyncDir);
            //}

            //if (!this.cancelSignal)
            {
                //upload
                this.updateUploadLog(string.Format("开始同步{0}下所有文件...", localSyncDir));
                this.processUpload(this.cacheFilePathDone);
            }

            //Dispatcher.Invoke(new Action(delegate ()
            //{
            //    HaltActionButton.IsEnabled = true;
            //}));

            //if (!this.cancelSignal && this.batchOpFiles.Count > 0)
            if (this.batchOpFiles.Count > 0)
            {
                //finish the remained
                this.uploadFiles(this.batchOpFiles);
                this.batchOpFiles.Clear();
            }

            //set finish signal
            this.finishSignal = true;
            this.closeLogWriters();
            if (this.localHashDB != null)
            {
                try
                {
                    this.localHashDB.Close();
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("job finish close local hash db failed {0}", ex.Message));
                }
            }
            if (this.syncLogDB != null)
            {
                try
                {
                    this.syncLogDB.Close();
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("job finish close sync log db failed {0}", ex.Message));
                }
            }

            //if (!this.cancelSignal)
            {
                //job auto finish, jump to result page
                DateTime jobEnd = System.DateTime.Now;
                this.mainWindow.GotoSyncResultPage(this.jobId, jobEnd - this.jobStart, this.syncSetting.OverwriteDuplicate,
                    this.fileSkippedCount, this.fileSkippedLogPath,
                    this.fileExistsCount, this.fileExistsLogPath,
                    this.fileOverwriteCount, this.fileOverwriteLogPath,
                    this.fileNotOverwriteCount, this.fileNotOverwriteLogPath,
                    this.fileUploadErrorCount, this.fileUploadErrorLogPath,
                    this.fileUploadSuccessCount, this.fileUploadSuccessLogPath);
            }
        }

        //halt or resume button click
        private void HaltActionButton_EventHandler(object sender, RoutedEventArgs e)
        {
            //this.HaltActionButton.IsEnabled = false;
            if (this.cancelSignal)
            {
                this.cancelSignal = false;
                this.HaltActionButton.Content = "暂停";
                this.ManualFinishButton.IsEnabled = false;
                //reset
                //this.resetSyncStatus();
                //this.HaltActionButton.IsEnabled = true;
                //this.HaltActionButton.Content = "暂停";
                //Thread jobThread = new Thread(new ParameterizedThreadStart(this.runSyncJob));
                //jobThread.Start(true);
            }
            else
            {
                this.cancelSignal = true;
                this.HaltActionButton.Content = "继续";
                this.ManualFinishButton.IsEnabled = true;
                //Thread checkThread = new Thread(new ThreadStart(delegate
                //{
                //    while (!this.finishSignal)
                //    {
                //        Thread.Sleep(1000);
                //    }
                //    Dispatcher.Invoke(new Action(delegate
                //    {
                //        this.HaltActionButton.IsEnabled = true;
                //        this.HaltActionButton.Content = "恢复";
                //        this.ManualFinishButton.IsEnabled = true;
                //    }));
                //}));
                //checkThread.Start();
            }
        }

        //manual finish button click
        private void ManualFinishButton_EventHandler(object sender, RoutedEventArgs e)
        {
            cancelSignal = true;
            finishSignal = true;

            DateTime jobEnd = System.DateTime.Now;
            this.mainWindow.GotoSyncResultPage(this.jobId, jobEnd - this.jobStart, this.syncSetting.OverwriteDuplicate,
               this.fileSkippedCount, this.fileSkippedLogPath,
               this.fileExistsCount, this.fileExistsLogPath,
               this.fileOverwriteCount, this.fileOverwriteLogPath,
               this.fileNotOverwriteCount, this.fileNotOverwriteLogPath,
               this.fileUploadErrorCount, this.fileUploadErrorLogPath,
               this.fileUploadSuccessCount, this.fileUploadSuccessLogPath);
        }

        //drop directory cache files
        private void dropcacheFilePath()
        {
            try
            {
                File.Delete(this.cacheFilePathDone);
            }
            catch (Exception ex)
            {
                Log.Error(string.Format("drop cache file {0} failed due to {1}", this.cacheFilePathDone, ex.Message));
            }
        }

        //write sync progress logs
        internal void addFileSkippedLog(string log)
        {
            lock (this.fileSkippedLock)
            {
                this.fileSkippedCount += 1;

                try
                {
                    this.fileSkippedWriter.WriteLine(log);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("write file skipped log for {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }

        internal void addFileExistsLog(string log)
        {
            lock (this.fileExistsLock)
            {
                this.fileExistsCount += 1;

                try
                {
                    this.fileExistsWriter.WriteLine(log);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("write file exists log for {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }

        internal void addFileOverwriteLog(string log)
        {
            lock (this.fileOverwriteLock)
            {
                this.fileOverwriteCount += 1;

                try
                {
                    this.fileOverwriteWriter.WriteLine(log);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("write file overwrite log for {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }

        internal void addFileNotOverwriteLog(string log)
        {
            lock (this.fileNotOverwriteLock)
            {
                this.fileNotOverwriteCount += 1;

                try
                {
                    this.fileNotOverwriteWriter.WriteLine(log);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("write file not overwrite log for {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }

        internal void addFileUploadErrorLog(string log)
        {
            lock (this.fileUploadErrorLock)
            {
                this.fileUploadErrorCount += 1;

                try
                {
                    this.fileUploadErrorWriter.WriteLine(log);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("write file upload failed log for {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }


        internal void addFileUploadSuccessLog(string log)
        {
            lock (this.fileUploadSuccessLock)
            {
                this.fileUploadSuccessCount += 1;

                try
                {
                    this.fileUploadSuccessWriter.WriteLine(log);
                }
                catch (Exception ex)
                {
                    Log.Error(string.Format("write file upload success log for {0} failed due to {1}", this.jobId, ex.Message));
                }
            }
        }

        //update ui status
        internal void updateUploadLog(string log)
        {
            lock (uploadLogLock)
            {
                Dispatcher.Invoke(new Action(delegate
                {
                    this.UploadProgressLogTextBlock.Text = log;
                }));
            }
        }

        internal void updateTotalUploadProgress()
        {
            lock (progressLock)
            {
                this.doneCount += 1;
            }
            Dispatcher.Invoke(new Action(delegate
            {
                double percent = this.doneCount * 100.0 / this.totalCount;
                this.UploadProgressTextBlock.Text = string.Format("总上传进度: {0}/{1}, {2}%", this.doneCount, this.totalCount, percent.ToString("F1"));
            }));
        }

        internal void updateSingleFileProgress(int taskId, string fileFullPath, string fileKey, long uploadedBytes, long fileLength)
        {
            lock (this.uploadInfoLock)
            {
                //calc
                string uploadProgress = string.Format("{0:0.000}%", 100.0 * uploadedBytes / fileLength);

                TimeSpan ts = DateTime.Now.Subtract(new DateTime(1970, 1, 1, 0, 0, 0));
                long newMills = (long)(ts.TotalMilliseconds);

                //set
                UploadInfo uploadInfo = this.uploadInfos[taskId];
                uploadInfo.LocalPath = fileFullPath;
                uploadInfo.FileKey = fileKey;
                uploadInfo.Progress = uploadProgress;
                if (this.uploadedBytes.ContainsKey(fileKey) && string.IsNullOrEmpty(uploadInfo.FinalSpeed))
                {
                    string lastUploadInfo = this.uploadedBytes[fileKey];
                    string[] lastUploadItems = lastUploadInfo.Split(':');

                    long oldUploaded = Convert.ToInt64(lastUploadItems[0]);
                    long oldMills = Convert.ToInt64(lastUploadItems[1]);

                    long deltaBytes = uploadedBytes - oldUploaded;
                    long deltaMillis = newMills - oldMills;

                    if (deltaMillis > 0 && deltaBytes > 0)
                    {
                        //KB/s
                        double speed = (deltaBytes / 1.024) / deltaMillis;
                        string speedStr = "";
                        if (speed > 1024)
                        {
                            speed = speed / 1024;
                            speedStr = string.Format("{0} MB/s", speed.ToString("F1"));
                        }
                        else
                        {
                            speedStr = string.Format("{0} KB/s", speed.ToString("F1"));
                        }

                        if (uploadedBytes < fileLength)
                        {
                            uploadInfo.Speed = speedStr;
                        }
                        else
                        {
                            uploadInfo.Speed = speedStr;
                            uploadInfo.FinalSpeed = speedStr;
                        }
                    }
                }
                else
                {
                    this.uploadedBytes.Add(fileKey, string.Format("{0}:{1}", uploadedBytes, newMills));
                    uploadInfo.Speed = "---";
                }
            }
            Dispatcher.Invoke(new Action(delegate
            {
                ObservableCollection<UploadInfo> dataSource = new ObservableCollection<UploadInfo>();
                foreach (UploadInfo info in this.uploadInfos)
                {
                    if (!string.IsNullOrEmpty(info.Progress))
                    {
                        dataSource.Add(info);
                    }
                }
                this.UploadProgressDataGrid.DataContext = dataSource;
            }));
        }

        internal bool checkCancelSignal()
        {
            return this.cancelSignal;
        }

        internal bool checkFinishSignal()
        {
            return this.finishSignal;
        }

        //close the log writers
        private void closeLogWriters()
        {
            try
            {
                if (this.fileSkippedWriter != null)
                {
                    this.fileSkippedWriter.Close();
                }
                if (this.fileExistsWriter != null)
                {
                    this.fileExistsWriter.Close();
                }
                if (this.fileOverwriteWriter != null)
                {
                    this.fileOverwriteWriter.Close();
                }
                if (this.fileNotOverwriteWriter != null)
                {
                    this.fileNotOverwriteWriter.Close();
                }
                if (this.fileUploadSuccessWriter != null)
                {
                    this.fileUploadSuccessWriter.Close();
                }
                if (this.fileUploadErrorWriter != null)
                {
                    this.fileUploadErrorWriter.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Error("close log writers failed, " + ex.Message);
            }
        }

    }
}