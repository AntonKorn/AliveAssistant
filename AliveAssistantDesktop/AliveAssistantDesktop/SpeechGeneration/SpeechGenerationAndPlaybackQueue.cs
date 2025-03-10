using Plugin.Maui.Audio;
using SherpaOnnx;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SpeechGeneration
{
    public class SpeechGenerationAndPlaybackQueue(OfflineTts offlineTts)
    {
        private ConcurrentQueue<string> _queue = [];

        private bool _isPlayerActive = false;

        private SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public bool IsPlaying => _isPlayerActive;

        public event Action<bool>? PlaybackFinished;

        public void Enqueue(string text)
        {
            _queue.Enqueue(text);

            _ = AttemptPlayingAsync();
        }

        public async Task ExecuteAtomicAsync(Func<Task> func)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);

            await func().ConfigureAwait(false);

            _semaphore.Release();
        }

        public async Task ExecuteAtomicAsync(Action func)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);

            func();

            _semaphore.Release();
        }

        private async Task<bool> AttemptPlayingAsync(bool fromContinuation = false)
        {
            var canContinue = false;

            await ExecuteAtomicAsync(() =>
            {
                canContinue = !_isPlayerActive || fromContinuation && !_queue.IsEmpty;
            });

            if (!canContinue)
            {
                return false;
            }

            _isPlayerActive = true;

            if (!_queue.IsEmpty && _queue.TryDequeue(out var item))
            {
                var fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @$"{Guid.NewGuid().ToString()}.wav");
                var textToGenerate = Regex.Replace(item, @"\[[\w:]+\]", string.Empty, RegexOptions.Multiline);

                await Task.Run(() =>
                {
                    var audio = offlineTts.Generate(
                    textToGenerate,
                    speed: 1,
                    speakerId: 0);

                    audio.SaveToWaveFile(fileName);
                    var activePlayer = AudioManager.Current.CreatePlayer(File.Open(fileName, FileMode.Open));
                    activePlayer.Play();

                    activePlayer.PlaybackEnded += OnPlaybackEnded;
                }).ConfigureAwait(false);
            }

            return true;
        }

        private async Task ContinuePlaybackAsync()
        {
            PlaybackFinished?.Invoke(_queue.IsEmpty);
            _isPlayerActive = await AttemptPlayingAsync(fromContinuation: true).ConfigureAwait(false);
        }

        private void OnPlaybackEnded(object? sender, EventArgs e)
        {
            _ = ContinuePlaybackAsync();
        }
    }
}
