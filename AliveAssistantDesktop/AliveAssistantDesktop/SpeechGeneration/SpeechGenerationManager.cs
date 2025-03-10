using System.Text;
using System.Text.RegularExpressions;

namespace SpeechGeneration
{
    public class SpeechGenerationManager
    {
        private readonly StringBuilder _textBuffer = new StringBuilder();
        private readonly IReadOnlyList<char> _sentenceEnders = ['.', ',', '!', '?'];
        private readonly char _responseEnder = '#';

        private SpeechGenerationState _recognitionState = SpeechGenerationState.Active;

        private bool _responseEnderFound = false;

        private readonly SpeechGenerationAndPlaybackQueue _speechGenerationAndPlaybackQueue;

        public SpeechGenerationManager(SpeechGenerationAndPlaybackQueue speechGenerationAndPlaybackQueue)
        {
            _speechGenerationAndPlaybackQueue = speechGenerationAndPlaybackQueue;

            _speechGenerationAndPlaybackQueue.PlaybackFinished += (isEmpty) =>
            {
                if (isEmpty && _recognitionState == SpeechGenerationState.PendingCompletion)
                {
                    Reset();
                    Completed?.Invoke();
                }
            };
        }

        private bool _isFirstGenerationAttempt = true;

        private void Reset()
        {
            _recognitionState = SpeechGenerationState.Active;
            _responseEnderFound = false;
            _textBuffer.Clear();
            _isFirstGenerationAttempt = true;
        }

        public event Action? Completed;

        public async Task AddTextAsync(string textChunk)
        {
            if (_recognitionState != SpeechGenerationState.Active)
            {
                throw new InvalidOperationException($"Text receival is forbidden for state {_recognitionState.ToString()}");
            }

            if (string.IsNullOrEmpty(textChunk) || _responseEnderFound)
            {
                return;
            }

            _responseEnderFound = textChunk.Contains(_responseEnder);
            var lastEnderIndex = FindLastEnderIndex(textChunk);

            if (lastEnderIndex.HasValue)
            {
                var bufferIncrement = _textBuffer.ToString() + textChunk.Substring(0, lastEnderIndex.Value + (!_responseEnderFound ? 1 : 0));

                if (_isFirstGenerationAttempt)
                {
                    bufferIncrement = Regex.Replace(bufferIncrement, @"^[\w]+:", string.Empty);
                    _isFirstGenerationAttempt = false;
                }

                _speechGenerationAndPlaybackQueue.Enqueue(bufferIncrement); // queue speach to text

                _textBuffer.Clear();

                if (!_responseEnderFound)
                {
                    _textBuffer.Append(textChunk.Substring(lastEnderIndex.Value + 1));
                }
            }
            else
            {
                _textBuffer.Append(textChunk);
            }
        }

        public async Task EndAsync()
        {
            await EndInternalAsync(false).ConfigureAwait(false); ;
        }

        private async Task EndInternalAsync(bool isLocked)
        {
            var hasDataToQueue = _responseEnderFound || _textBuffer.Length == 0;

            if (!hasDataToQueue && !_speechGenerationAndPlaybackQueue.IsPlaying)
            {
                Reset();
                Completed?.Invoke();
                return;
            }

            if (hasDataToQueue)
            {
                _recognitionState = SpeechGenerationState.PendingCompletion;
                _speechGenerationAndPlaybackQueue.Enqueue(_textBuffer.ToString());
                return;
            }

            if (!hasDataToQueue && _speechGenerationAndPlaybackQueue.IsPlaying)
            {
                if (isLocked)
                {
                    _recognitionState = SpeechGenerationState.PendingCompletion;
                }
                else
                {
                    await _speechGenerationAndPlaybackQueue.ExecuteAtomicAsync(async () => await EndInternalAsync(true)).ConfigureAwait(false);
                }
            }
        }

        private int? FindLastEnderIndex(string text)
        {
            return _sentenceEnders.Append(_responseEnder)
                .Select(ender => text.LastIndexOf(ender))
                .Where(index => index >= 0)
                .Select(x => (int?)x)
                .OrderBy(x => x)
                .FirstOrDefault();
        }

        private enum SpeechGenerationState
        {
            Active = 1,
            PendingCompletion,
            Completed,
        }
    }
}
