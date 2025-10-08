using RevitMCPSDK.API.Interfaces;
using System;
using System.Diagnostics;
using System.IO;

namespace ConnectXRevit.Utils
{
    public class Logger : ILogger
    {
        private readonly string _logFilePath;
        private LogLevel _currentLogLevel = LogLevel.Debug;

        public Logger()
        {
            _logFilePath = Path.Combine(PathManager.GetLogsDirectoryPath(), $"cx_{DateTime.Now:yyyyMMdd}.log");

        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            if (level < _currentLogLevel)
                return;

            string formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            string callerClassName = GetCallerClassName();
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{callerClassName}]: {formattedMessage}";

            // 输出到 Debug 窗口
            // Output to debug window.
            System.Diagnostics.Debug.WriteLine(logEntry);

            // 写入日志文件
            // Write to the logfile.
            try
            {
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // 如果写入日志文件失败，不抛出异常
                // If writing to the logfile fails, do not throw an exception.
            }
        }

        // 新增方法：获取调用类名
        private string GetCallerClassName()
        {
            try
            {
                // 获取调用栈（跳过当前方法和Log方法）
                var stackTrace = new StackTrace();
                // 0: GetCallerClassName, 1: Log, 2: 调用者（需要获取的类）
                for (int i = 2; i < stackTrace.FrameCount; i++)
                {
                    var frame = stackTrace.GetFrame(i);
                    if (frame == null) continue;

                    var declaringType = frame.GetMethod().DeclaringType;
                    if (declaringType == null) continue;

                    // 跳过Logger类本身
                    if (declaringType.Name != "Logger")
                    {
                        return declaringType.Name;
                    }
                }
                return "Unknown";
            }
            catch
            {
                // 出错时返回默认值
            }
            return "Unknown";
        }

        public void Debug(string message, params object[] args)
        {
            Log(LogLevel.Debug, message, args);
        }

        public void Info(string message, params object[] args)
        {
            Log(LogLevel.Info, message, args);
        }

        public void Warning(string message, params object[] args)
        {
            Log(LogLevel.Warning, message, args);
        }

        public void Error(string message, params object[] args)
        {
            Log(LogLevel.Error, message, args);
        }
    }
}
