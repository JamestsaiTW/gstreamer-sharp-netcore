using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Native;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Gst;
using Gst.App;
using Gst.Video;

namespace GStreamerPlayer;

public partial class MainWindow : Window
{
    #region Runtime fields

    private const int maxFPS = 100;
    private Avalonia.Rendering.DefaultRenderTimer renderTimer = null;
    private long renderLock = 0;

    #endregion

    #region GStreamer pipeline items

    private Pipeline Pipeline;
    private Element Playbin;
    private AppSink VideoSink;

    #endregion

    #region Public properties

    public bool IsLive { get; private set; }
    public string SourceUri { get; private set; }

    #endregion

    public MainWindow()
    {
        InitializeComponent();

        InitializeControls();

        InitGst();
    }

    private void InitGst()
    {
        Gst.Application.Init();
        GtkSharp.GstreamerSharp.ObjectManager.Initialize();

        renderTimer = new Avalonia.Rendering.UiThreadRenderTimer(maxFPS);

        renderTimer.Tick += (TimeSpan time) =>
        {
            if (Pipeline != null)
            {
                var message = Pipeline.Bus.Poll(MessageType.Any, 0);
                if (message != null)
                {
                    OnMessage(message);
                    message.Dispose();
                }
            }

            if (FrameImage != null &&
                System.Threading.Interlocked.CompareExchange(ref renderLock, 1, 0) == 0)
            {

                try
                {
                    if (Pipeline != null && VideoSink != null)
                    {
                        PullVideoSample();
                    }
                }
                finally
                {
                    System.Threading.Interlocked.Decrement(ref renderLock);
                }
            }
        };

        CreatePipeline();
    }

    private void InitializeControls()
    {
        MenuUri.GotFocus += (sender, args) => { UrisOfChoice.Text = string.Empty; };

        UrisOfChoice.FilterMode = AutoCompleteFilterMode.ContainsOrdinal;
        UrisOfChoice.ItemsSource = new string[]
        {
            "https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd",
            "http://distribution.bbb3d.renderfarming.net/video/mp4/bbb_sunflower_2160p_60fps_normal.mp4"
        };
        UrisOfChoice.KeyUp += (sender, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
            {
                HandleUriBox();
            }
            else if (args.Key == Avalonia.Input.Key.Escape)
            {
                UrisOfChoice.Text = string.Empty;
            }
        };
        UrisOfChoice.TextChanged += (sender, args) =>
        {
            var text = UrisOfChoice.Text?.Trim();
            OpenUriButton.IsEnabled = !string.IsNullOrEmpty(text);
        };
        OpenUriButton.Click += (sender, args) => { HandleUriBox(); };

        MenuOpenFile.Click += OnOpenFileClick;
        MenuExit.Click += (sender, arg) =>
        {
            if (Pipeline != null)
            {
                Stop();
            }

            Close();
        };
    }


    private void HandleUriBox()
    {
        var text = UrisOfChoice.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            Play(text);
            UrisOfChoice.Text = string.Empty;
        }

        MainMenu.Close();
    }

    private async void OnOpenFileClick(object sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog();
        dialog.AllowMultiple = false;
        dialog.Title = "Choose a media file";
        var files = await dialog.ShowAsync(this);

        if (files != null && files.Length > 0)
        {
            var file = files[0];
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform
                    .Windows))
            {
                Play($"file:/{file.Replace('\\', '/')}");
            }
            else
            {
                Play($"file://{file}");
            }
        }
    }

    private void CreatePipeline()
    {
        if (Pipeline != null)
        {
            Pipeline.SetState(State.Null);
            IsLive = false;
            return;
        }

        Pipeline = new Pipeline("playback");
        Playbin = ElementFactory.Make("playbin", "playbin");
        Pipeline.Add(Playbin);

        VideoSink = new AppSink("videoSink");
        VideoSink["caps"] = Caps.FromString("video/x-raw,format=RGBA");

        VideoSink.Drop = true; // drop frames if cannot keep up
        VideoSink.Sync = true; // synchronized playback 
        VideoSink.MaxLateness = (1000 / 30) * 1000000; // maximum latency to achieve at least 30 fps
        VideoSink.MaxBuffers = 1; // no buffering for video sink
        VideoSink.Qos = true; // QoS for video sink 
        VideoSink.EnableLastSample = false; // no need for last sample as we are pulling samples 

        Playbin["video-sink"] = VideoSink;

        var ret = Pipeline.SetState(State.Ready);
    }

    private bool Play(string source)
    {
        Stop();
        if (Pipeline == null)
        {
            return false;
        }

        SourceUri = source;
        Playbin["uri"] = SourceUri;
        var ret = Pipeline.SetState(State.Playing);

        if (ret == StateChangeReturn.Failure)
        {
            Console.WriteLine("Unable to set the pipeline to the playing state.");
        }
        else if (ret == StateChangeReturn.NoPreroll)
        {
            IsLive = true;
            Console.WriteLine("Playing a live stream.");
            ret = StateChangeReturn.Success;
        }

        return ret == StateChangeReturn.Success;
    }

    private void Stop()
    {
        IsLive = false;
        SourceUri = string.Empty;

        if (Pipeline != null)
        {
            Pipeline.SetState(State.Null);
        }

        FrameImage.Clear();
    }

    private void PullVideoSample()
    {
        var sink = VideoSink;

        if (sink == null)
        {
            return;
        }

        Sample sample = sink.TryPullSample(0);

        if (sample == null)
        {
            return;
        }

        Caps caps = sample.Caps;
        var cap = caps[0];

        string format;
        int width = 0;
        int height = 0;
        int fpsNumerator = 0;
        int fpsDenominator = 1;

        format = cap.GetString("format");
        cap.GetInt("width", out width);
        cap.GetInt("height", out height);
        cap.GetFraction("framerate", out fpsNumerator, out fpsDenominator);

        using (var buffer = sample.Buffer)
        {
            MapInfo map;
            if (format == "RGBA" && buffer.Map(out map, MapFlags.Read))
            {
                FrameImage.UpdateImage(ref map, width, height);
                buffer.Unmap(map);
            }
        }

        sample.Dispose();
    }

    private void OnMessage(Gst.Message message)
    {
        switch (message.Type)
        {
            case MessageType.StateChanged:
                State oldstate, newstate, pendingstate;
                message.ParseStateChanged(out oldstate, out newstate, out pendingstate);
                System.Console.WriteLine($"[StateChange] From {oldstate} to {newstate} pending at {pendingstate}");
                break;
            case MessageType.StreamStatus:
                Element owner;
                StreamStatusType type;
                message.ParseStreamStatus(out type, out owner);
                System.Console.WriteLine($"[StreamStatus] Type {type} from {owner}");
                break;
            case MessageType.DurationChanged:
                long duration;
                Pipeline.QueryDuration(Format.Time, out duration);
                System.Console.WriteLine(
                    $"[DurationChanged] New duration is {(duration / Gst.Constants.SECOND)} seconds");
                break;
            case MessageType.ResetTime:
                ulong runningtime = message.ParseResetTime();
                System.Console.WriteLine($"[ResetTime] Running time is {runningtime}");
                break;
            case MessageType.AsyncDone:
                ulong desiredrunningtime = message.ParseAsyncDone();
                System.Console.WriteLine($"[AsyncDone] Running time is {desiredrunningtime}");
                break;
            case MessageType.NewClock:
                Clock clock = message.ParseNewClock();
                System.Console.WriteLine($"[NewClock] {clock}");
                break;
            case MessageType.Buffering:
                int percent = message.ParseBuffering();
                System.Console.WriteLine($"[Buffering] {percent}% done");

                if (!IsLive)
                {
                    if (percent < 100)
                    {
                        Pipeline.SetState(State.Paused);
                    }
                    else
                    {
                        Pipeline.SetState(State.Playing);
                    }

                    break;
                }

                break;
            case MessageType.Tag:
                TagList list = message.ParseTag();
                System.Console.WriteLine($"[Tag] Information in scope {list.Scope} is {list.ToString()}");
                break;
            case MessageType.Error:
                GLib.GException gerror;
                string debug;
                message.ParseError(out gerror, out debug);
                System.Console.WriteLine($"[Error] {gerror.Message}, debug information {debug}.");
                break;
            case MessageType.Eos:
                System.Console.WriteLine("[Eos] Playback has ended.");
                break;
            default:
                System.Console.WriteLine($"[Recv] {message.Type}");
                break;
        }
    }
}
