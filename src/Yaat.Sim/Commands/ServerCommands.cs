namespace Yaat.Sim.Commands;

public record DeleteCommand : ParsedCommand;

public record PauseCommand : ParsedCommand;

public record UnpauseCommand : ParsedCommand;

public record SimRateCommand(int Rate) : ParsedCommand;

public record SpawnNowCommand : ParsedCommand;

public record SpawnDelayCommand(int Seconds) : ParsedCommand;

public record ConsolidateCommand(string ReceivingTcpCode, string SendingTcpCode, bool Full) : ParsedCommand;

public record DeconsolidateCommand(string TcpCode) : ParsedCommand;
