extern alias HexGame;
using UID = HexGame::Game.Shared.UID;

namespace Dingler.Game.Games;

public sealed class GameTimer : IDisposable
{
	private readonly UID _playerId; 
	private TimeSpan _inactivityTime = new (0, 0, 5, 0);
	private TimeSpan _initialMatchTime = new (0, 20, 0);

	public TimeSpan MatchClockLimit
	{
		get => _initialMatchTime;
		set => _initialMatchTime = value;
	}

	// Arena battles use 45 minutes for the human and effectively none for the AI seat (design 03 D17-A).
	// Keep values under ~24 days: they go to Task.Delay.
	public TimeSpan InactivityLimit
	{
		get => _inactivityTime;
		set => _inactivityTime = value;
	}
	private DateTime _lastRun;
	private CancellationTokenSource? _cts;
	private bool _isChessTimerRunning;
	public bool HasTimeExpired { get; private set; }

	public TimeSpan ElapsedTime
	{
		get
		{
			if (_cts is null || _cts.IsCancellationRequested)
			{
				return field;
			}

			return field + (DateTime.UtcNow - _lastRun);
		}
		set;
	}

	public event Action<UID>? PlayerRanOutOfTime;
	public event Action<UID>? TimerStarted;
	public event Action<UID>? TimerStopped;

	public GameTimer(UID playerId)
	{
		_playerId = playerId;
	}

	public void StartChessTimer()
	{
		if (_isChessTimerRunning)
			return;
		
		_isChessTimerRunning = true;
		_lastRun = DateTime.UtcNow;
		_cts = new CancellationTokenSource();
		var remainingMatchTime = _initialMatchTime - ElapsedTime;
		if (remainingMatchTime < TimeSpan.Zero)
			remainingMatchTime = TimeSpan.Zero;
		_ = StartTimerAsync(remainingMatchTime, _cts.Token);
		_ = StartTimerAsync(_inactivityTime, _cts.Token);
		TimerStarted?.Invoke(_playerId);
	}
	
	public void StopChessTimer()
	{
		if (!_isChessTimerRunning)
			return;
		
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
		_isChessTimerRunning = false;
		
		ElapsedTime += DateTime.UtcNow - _lastRun;
		TimerStopped?.Invoke(_playerId);
	}

	public void AddFudgeTime()
	{
		var wasTimerRunningBeforeCall = _isChessTimerRunning;

		if (wasTimerRunningBeforeCall)
		{
			StopChessTimer();
		}

		ElapsedTime = ElapsedTime.Add(new TimeSpan(0, 0, 0, -10));
		
		if (wasTimerRunningBeforeCall)
			StartChessTimer();
	}
	
	private async Task StartTimerAsync(TimeSpan timeSpan, CancellationToken token)
	{
		try
		{
			await Task.Delay(timeSpan, token);
			HasTimeExpired = true;
			PlayerRanOutOfTime?.Invoke(_playerId);
		}
		catch (OperationCanceledException)
		{
			
		}
	}

	public void ResetTimer()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
		
		ElapsedTime = TimeSpan.Zero;
		_isChessTimerRunning = false;
	}

	public void Dispose()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
	}
}