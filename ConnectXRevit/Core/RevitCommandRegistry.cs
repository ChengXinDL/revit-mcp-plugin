using RevitMCPSDK.API.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace ConnectXRevit.Core
{
    public class RevitCommandRegistry : ICommandRegistry
    {
        private readonly Dictionary<string, IRevitCommand> _commands = new Dictionary<string, IRevitCommand>();
        private readonly ILogger _logger; // 新增：注入日志器

        // 修改构造函数以接收 ILogger
        public RevitCommandRegistry(ILogger logger)
        {
            _logger = logger;
        }

        public void RegisterCommand(IRevitCommand command)
        {
            _commands[command.CommandName] = command;
            _logger?.Info($"已成功注册命令: '{command.CommandName}'");
        }

        public bool TryGetCommand(string commandName, out IRevitCommand command)
        {
            return _commands.TryGetValue(commandName, out command);
        }

        public void ClearCommands()
        {
            _commands.Clear();
        }

        public IEnumerable<string> GetRegisteredCommands()
        {
            var commands = _commands.Keys;
            _logger?.Info($"获取已注册命令列表，共 {commands.Count()} 个: [{string.Join(", ", commands)}]");
            return commands;
        }
    }
}
