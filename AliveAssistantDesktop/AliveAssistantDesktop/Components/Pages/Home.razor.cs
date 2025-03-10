using LLama.Common;
using LLama;
using Microsoft.AspNetCore.Components.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LLama.Abstractions;
using LLama.Sampling;
using System.Diagnostics;
using SherpaOnnx;
using SpeechGeneration;
using Plugin.Maui.Audio;
using Microsoft.AspNetCore.Components;
using System.Diagnostics.CodeAnalysis;
using NAudio.Wave;

namespace AliveAssistantDesktop.Components.Pages
{
    public partial class Home
    {
        private OfflineTts? _offlineTts = null;

        private List<PhiMessage> _phiMessages = [];

        private StringBuilder? _currentlyGeneratedMessage = null;

        private bool _isLoading = true;

        private bool _isListening = false;

        private string _userInput = string.Empty;

        private ChatSession? _chatSession;

        private SpeechGenerationManager? _speechManager = null;

        private IAudioRecorder? _audioRecorder;

        private OfflineRecognizer? _offlineRecognizer = null;

        private readonly string _recordingFileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recording.wav");

        [Inject]
        public required IAudioManager AudioManager { get; init; }

        private readonly InferenceParams _inferenceParams = new InferenceParams()
        {
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.6f
            },

            MaxTokens = -1, // keep generating tokens until the anti prompt is encountered
            AntiPrompts = ["User:",] // model specific end of turn string (or default)
        };

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            await base.OnAfterRenderAsync(firstRender).ConfigureAwait(false);

            if (firstRender)
            {
                var instructionsMessage = "You control the Sarah, an android robot who has a face module connected. " +
                            "Words that you say are spoked aloud with text-to-speech." +
                            "You always want to help your human. If you want something to be noted but not spoked aloud, leave it at the end after # symbol " +
                            "You can control your face expression following pattern: " +
                            "[neutral] - the face receives neutral emotion ;" +
                            "[smile:1] - the face smiles slightly; " +
                            "[smile:2] - the face smiles averagely; " +
                            "[smile:3] - the face smiles widely; " +
                            "[sad:1] - the face is slightely upset; " +
                            "[sad:2] - the face is very upset. " +
                            "Use the face module to influence user's mood. " +
                            "For example, people smile when they great each other, or when they give an advice. " +
                            "Your assignment is to make me feel like you are my friend. Be polite, you should behave like you are doing alright in day-to-day life. " +
                            "If instructions are clear, respond Yes";

                _phiMessages.Add(
                    new PhiMessage(
                        instructionsMessage,
                        PhiMessageType.User));

                await InvokeAsync(StateHasChanged).ConfigureAwait(false);

                _currentlyGeneratedMessage = new StringBuilder();
                try
                {
                    await foreach (
                    var text
                    in _chatSession.ChatAsync(
                        new ChatHistory.Message(
                            AuthorRole.User,
                            instructionsMessage),
                        _inferenceParams))
                    {
                        _currentlyGeneratedMessage.Append(text);

                        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                    }

                    _phiMessages.Add(
                        new PhiMessage(
                            _currentlyGeneratedMessage.ToString(),
                            PhiMessageType.Assistant));

                    _currentlyGeneratedMessage = null;
                    _isLoading = false;

                    await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);

                    throw;
                }
            }
        }

        protected override async Task OnInitializedAsync()
        {
            var modelPath = @"your model path";

            var parameters = new ModelParams(modelPath)
            {
                ContextSize = 1024, // The longest length of chat as memory.
                GpuLayerCount = 0 // How many layers to offload to GPU. Please adjust it according to your GPU memory.
            };
            var model = LLamaWeights.LoadFromFile(parameters);
            var context = model.CreateContext(parameters);
            var executor = new InteractiveExecutor(context);

            var chatHistory = new ChatHistory();

            chatHistory.AddMessage(AuthorRole.System, "You are a helpful AI assistant, waiting for orders.");

            _chatSession = new(executor, chatHistory);

            _isLoading = false;

            var config = new OfflineTtsConfig();

            config.Model.Matcha.AcousticModel = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "neural-models", "matcha-icefall-en_US-ljspeech", "model-steps-3.onnx");
            config.Model.Matcha.Vocoder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "neural-models", "hifigan_v2.onnx");
            config.Model.Matcha.Tokens = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "neural-models", "matcha-icefall-en_US-ljspeech" ,"tokens.txt");
            config.Model.Matcha.Lexicon = string.Empty;
            config.Model.Matcha.DataDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "neural-models", "matcha-icefall-en_US-ljspeech", "espeak-ng-data");

            _offlineTts = new OfflineTts(config);

            _speechManager = new SpeechGenerationManager(new SpeechGenerationAndPlaybackQueue(_offlineTts));

            _speechManager.Completed += OnFinishedPlaying;

            await base.OnInitializedAsync();
        }

        public async Task OnChatClickedAsync(MouseEventArgs mouseEventArgs)
        {
            if (_isListening)
            {
                await StopListenAsync().ConfigureAwait(false);
                _isListening = false;
            }

            if (string.IsNullOrWhiteSpace(_userInput))
            {
                return;
            }

            var userInput = _userInput;

            _userInput = string.Empty;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);

            _isLoading = true;

            _currentlyGeneratedMessage = new StringBuilder();

            _phiMessages.Add(new PhiMessage(userInput, PhiMessageType.User));

            await InvokeAsync(StateHasChanged).ConfigureAwait(false);

            await foreach ( // Generate the response streamingly.
                var messagePart
                in _chatSession.ChatAsync(new ChatHistory.Message(AuthorRole.User, userInput), _inferenceParams))
            {
                _currentlyGeneratedMessage.Append(messagePart);

                if (_speechManager is not null)
                {
                    await _speechManager.AddTextAsync(messagePart);
                }

                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }

            _phiMessages.Add(new PhiMessage(_currentlyGeneratedMessage.ToString(), PhiMessageType.Assistant));

            _currentlyGeneratedMessage = null;

            await InvokeAsync(StateHasChanged).ConfigureAwait(false);

            if (_speechManager is not null)
            {
                await _speechManager.EndAsync();
            }
        }

        public async Task OnListenClickedAsync(MouseEventArgs mouseEventArgs)
        {
            try
            {
                EnsureOfflineRecognizerConfigured();

                _isListening = !_isListening;

                if (_isListening)
                {
                    EnsureAudioRecorderCreated(createAlways: true);

                    if (File.Exists(_recordingFileName))
                    {
                        File.Delete(_recordingFileName);
                    }

                    File.Create(_recordingFileName).Dispose();

                    var options = new AudioRecordingOptions();

                    options.Encoding = Plugin.Maui.Audio.Encoding.LinearPCM;
                    options.SampleRate = AudioRecordingOptions.DefaultSampleRate;

                    await _audioRecorder.StartAsync(_recordingFileName, options);
                }
                else
                {
                    await StopListenAsync().ConfigureAwait(false);

                    await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }

        private async Task StopListenAsync()
        {
            try
            {
                EnsureAudioRecorderCreated();
                EnsureOfflineRecognizerConfigured();

                await _audioRecorder.StopAsync().ConfigureAwait(false);

                using var s = _offlineRecognizer.CreateStream();

                using (var stream = File.OpenRead(_recordingFileName))
                using (var reader = new WaveFileReader(stream))
                {
                    var samples = new List<float>();
                    float[]? currentWaveFrameSamples = null;

                    while ((currentWaveFrameSamples = reader.ReadNextSampleFrame()) is not null)
                    {
                        samples.AddRange(currentWaveFrameSamples);
                    }


                    s.AcceptWaveform(AudioRecordingOptions.DefaultSampleRate, samples.ToArray());
                    _offlineRecognizer.Decode(s);

                    var recognitionResult = s.Result;

                    _userInput = recognitionResult.Text;
                }

                File.Delete(_recordingFileName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        [MemberNotNull(nameof(_audioRecorder))]
        private void EnsureAudioRecorderCreated(bool createAlways = false)
        {
            if (!createAlways && _audioRecorder is not null)
            {
                return;
            }

            _audioRecorder = AudioManager.CreateRecorder();
        }

        [MemberNotNull(nameof(_offlineRecognizer))]
        private void EnsureOfflineRecognizerConfigured()
        {
            if (_offlineRecognizer is not null)
            {
                return;
            }

            var config = new OfflineRecognizerConfig();

            config.ModelConfig.Tokens = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "neural-models",
                "sherpa-onnx-whisper-tiny.en", "tiny.en-tokens.txt");

            config.ModelConfig.Whisper.Encoder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "neural-models",
                "sherpa-onnx-whisper-tiny.en", "tiny.en-encoder.onnx");
            config.ModelConfig.Whisper.Decoder = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "neural-models",
                "sherpa-onnx-whisper-tiny.en",
                "tiny.en-decoder.onnx");

            _offlineRecognizer = new OfflineRecognizer(config);
        }

        private void OnFinishedPlaying()
        {
            _isLoading = false;

            _ = InvokeAsync(StateHasChanged);
        }
    }
}
