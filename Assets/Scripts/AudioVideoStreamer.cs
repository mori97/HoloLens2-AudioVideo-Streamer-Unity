using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using UnityEngine;
#if ENABLE_WINMD_SUPPORT
using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Foundation;
using Windows.Media.MediaProperties;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
#endif

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

public class AudioVideoStreamer : MonoBehaviour
{
    public string audioServiceName = "50001";
    public uint videoWidth = 960;
    public uint videoHeight = 540;
    public double frameRate = 15.0;
#if ENABLE_WINMD_SUPPORT
    private MediaCapture mediaCapture;
    private MediaFrameReader audioFrameReader;
    private StreamSocketListener audioSocketListener;
    private IOutputStream audioStream;

    async Task Start()
    {
        // Socket listener
        audioSocketListener = new StreamSocketListener();
        audioSocketListener.ConnectionReceived += OnConnectionAudio;
        await audioSocketListener.BindServiceNameAsync(audioServiceName);

        // Find a media source group which gives us webcam and microphone input streams
        var sourceGroups = await MediaFrameSourceGroup.FindAllAsync();

        MediaFrameSourceGroup selectedSourceGroup = null;
        MediaCaptureVideoProfile selectedVideoProfile = null;
        MediaCaptureVideoProfileMediaDescription selectedDescription = null;

        foreach (MediaFrameSourceGroup sourceGroup in sourceGroups)
        {
            var videoProfiles = MediaCapture.FindKnownVideoProfiles(sourceGroup.Id, KnownVideoProfile.VideoConferencing);
            foreach (MediaCaptureVideoProfile videoProfile in videoProfiles)
            {
                foreach (var desc in videoProfile.SupportedRecordMediaDescription)
                {
                    if (desc.Width == videoWidth && desc.Height == videoHeight && desc.FrameRate == frameRate)
                    {
                        selectedSourceGroup = sourceGroup;
                        selectedVideoProfile = videoProfile;
                        selectedDescription = desc;
                    }
                }
            }
        }

        if (selectedSourceGroup == null)
        {
            Debug.Log("No source group was found.");
            return;
        }

        mediaCapture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings()
        {
            AudioProcessing = AudioProcessing.Raw,
            RecordMediaDescription = selectedDescription,
            SourceGroup = selectedSourceGroup,
            StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,
            VideoProfile = selectedVideoProfile,
        };
        try
        {
            await mediaCapture.InitializeAsync(settings);
        }
        catch (Exception ex)
        {
            Debug.Log("MediaCapture initialization failed: " + ex.Message);
            return;
        }

        var audioFrameSources = mediaCapture.FrameSources.Where(src => src.Value.Info.MediaStreamType == MediaStreamType.Audio);
        if (audioFrameSources.Count() == 0)
        {
            Debug.Log("No audio source was found.");
            return;
        }
        MediaFrameSource audioFrameSource = audioFrameSources.FirstOrDefault().Value;

        // Start streaming
        audioFrameReader = await mediaCapture.CreateFrameReaderAsync(audioFrameSource);
        audioFrameReader.FrameArrived += AudioFrameArrived;
        var status = await audioFrameReader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            Debug.Log("The audioFrameReader couldn't start.");
        }
    }

    private async void OnConnectionAudio(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var streamSocket = args.Socket;
        audioStream = streamSocket.OutputStream;
        Debug.Log("Received Audio Connection.");
    }

    private void AudioFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using (MediaFrameReference reference = sender.TryAcquireLatestFrame())
        {
            if (reference != null)
            {
                ProcessAudioFrame(reference.AudioMediaFrame);
            }
        }
    }

    unsafe private void ProcessAudioFrame(AudioMediaFrame audioMediaFrame)
    {
        using (AudioFrame audioFrame = audioMediaFrame.GetAudioFrame())
        using (AudioBuffer buffer = audioFrame.LockBuffer(AudioBufferAccessMode.Read))
        using (IMemoryBufferReference reference = buffer.CreateReference())
        {
            
            byte* dataInBytes;
            uint capacityInBytes;
            ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);
            float* dataInFloat = (float*)dataInBytes;

            TimeSpan duration = audioMediaFrame.FrameReference.Duration;
            uint frameDurMs = (uint)duration.TotalMilliseconds;
            uint sampleRate = audioMediaFrame.AudioEncodingProperties.SampleRate;
            uint sampleCount = (frameDurMs * sampleRate) / 1000;

            // Only send input signals of 1st~5th channel
            byte[] buf = new byte[4 * (5 * sampleCount + 3)];
            fixed (byte* pBufByte = buf)
            {
                float* pBufFloat = (float*)pBufByte; 
                uint* pBufUint = (uint*)pBufByte;
                pBufUint[0] = 5;
                pBufUint[1] = sampleCount;
                pBufUint[2] = sampleRate;
                for (uint i = 0; i < 5 * sampleCount; i++)
                {
                    uint frameIdx = i / 5;
                    uint channelIdx = i % 5;
                    pBufFloat[3 + 5 * frameIdx + channelIdx] = dataInFloat[11 * frameIdx + channelIdx];
                }
            }
            audioStream.WriteAsync(buf.AsBuffer());
        }
    }

    async Task OnDestroy()
    {
        await audioFrameReader.StopAsync();
        audioFrameReader.FrameArrived -= AudioFrameArrived;
        mediaCapture.Dispose();
        audioStream.FlushAsync();
        audioStream.Dispose();
    }
#endif
}
