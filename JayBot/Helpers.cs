using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JayBot
{
    class GlobalMutexHelper : IDisposable
    {
        private Mutex mutex = null;
        private bool active = false;

        public GlobalMutexHelper(string mutexNameWithoutGlobal, int? timeout = 5000)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            mutex = new Mutex(false, $"Global\\{mutexNameWithoutGlobal}");
            MutexSecurity sec = new MutexSecurity();
            sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
            mutex.SetAccessControl(sec);

            try
            {
                active = mutex.WaitOne(timeout == null || timeout < 0 ? Timeout.Infinite : timeout.Value, false);
            }
            catch (AbandonedMutexException e)
            {
                active = true;
            }

            if (!active)
            {
                throw new Exception($"Failed to acquire acquire mutex \"{mutexNameWithoutGlobal}\".");
            }
            Int64 elapsed = sw.ElapsedMilliseconds;
            sw.Stop();
            if (elapsed > 500)
            {
                Helpers.logToFile($"WARNING: Acquiring global mutex '{mutexNameWithoutGlobal}' took {elapsed} milliseconds.\n");
            }
        }
        public void Dispose()
        {
            if (mutex != null)
            {
                if (active)
                {
                    mutex.ReleaseMutex();
                }
                mutex.Dispose();
            }
        }
    }
    static class Helpers
    {
        private static readonly Dictionary<string, string> cachedFileReadCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, DateTime> cachedFileReadLastRead = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, DateTime> cachedFileReadLastDateModified = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, bool> cachedFileReadFileExists = new Dictionary<string, bool>();
        const int cachedFileReadTimeout = 5000;
        const int cachedFileReadRetryDelay = 100;
        public static string cachedFileRead(string path)
        {
            string data = null;
            DateTime? lastModified = null;
            bool fileExists = false;
            try
            {

                try
                {

                    lock (cachedFileReadCache)
                    {
                        if (cachedFileReadCache.ContainsKey(path) && cachedFileReadLastRead.ContainsKey(path) && cachedFileReadFileExists.ContainsKey(path))
                        {
                            TimeSpan delta = DateTime.Now - cachedFileReadLastRead[path];
                            if (delta.TotalSeconds < 10)
                            {
                                return cachedFileReadCache[path];
                            }
                            else if (delta.TotalMinutes < 10)
                            {
                                if (File.Exists(path))
                                {
                                    if (cachedFileReadFileExists[path])
                                    {
                                        DateTime lastWriteTime = File.GetLastWriteTime(path);
                                        if (cachedFileReadLastDateModified.ContainsKey(path) && lastWriteTime == cachedFileReadLastDateModified[path])
                                        {
                                            return cachedFileReadCache[path];
                                        }
                                    }
                                }
                                else
                                {
                                    if (!cachedFileReadFileExists[path])
                                    {
                                        return null; // File didn't exist. Still doesn't.
                                    }
                                }
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    /// whatever
                }


                if (!File.Exists(path))
                {
                    data = null;
                }
                else
                {
                    fileExists = true;

                    using (new GlobalMutexHelper("JKWatcherCachedFileReadMutex"))
                    {

                        int retryTime = 0;
                        bool successfullyRead = false;
                        while (!successfullyRead && retryTime < cachedFileReadTimeout)
                        {
                            try
                            {

                                data = File.ReadAllText(path);
                                lastModified = File.GetLastWriteTime(path);

                                successfullyRead = true;
                            }
                            catch (IOException)
                            {
                                // Wait 100 ms then try again. File is probably locked.
                                // This will probably lock up the thread a bit in some cases
                                // but the log display/write thread is separate from the rest of the 
                                // program anyway so it shouldn't have a terrible impact other than a delayed
                                // display.
                                System.Threading.Thread.Sleep(cachedFileReadRetryDelay);
                                retryTime += cachedFileReadRetryDelay;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Failed to get  mutex, weird...
            }

            lock (cachedFileReadCache)
            {
                cachedFileReadCache[path] = data;
                cachedFileReadLastRead[path] = DateTime.Now;
                cachedFileReadFileExists[path] = fileExists;
                if (lastModified.HasValue)
                {
                    cachedFileReadLastDateModified[path] = lastModified.Value;
                }
            }

            return data;
        }




        public static void logToFile(string text)
        {
            Helpers.logToFile(new string[] { text });
        }
        private static int logfileWriteRetryDelay = 100;
        private static int logfileWriteTimeout = 5000;
        public static string forcedLogFileName = "forcedLog.log";
        public static void logToFile(string[] texts)
        {
            Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JayBot"));
            foreach (string line in texts)
            {
                Debug.WriteLine(line);
            }
            try
            {

                //lock (forcedLogFileName)
                using (new GlobalMutexHelper("JayBotForcedLogMutex"))
                {
                    int retryTime = 0;
                    bool successfullyWritten = false;
                    while (!successfullyWritten && retryTime < logfileWriteTimeout)
                    {
                        try
                        {

                            File.AppendAllLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JayBot", forcedLogFileName), texts);
                            successfullyWritten = true;
                        }
                        catch (IOException)
                        {
                            // Wait 100 ms then try again. File is probably locked.
                            // This will probably lock up the thread a bit in some cases
                            // but the log display/write thread is separate from the rest of the 
                            // program anyway so it shouldn't have a terrible impact other than a delayed
                            // display.
                            System.Threading.Thread.Sleep(logfileWriteRetryDelay);
                            retryTime += logfileWriteRetryDelay;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Failed to get  mutex, weird...
            }
        }
    }
}
