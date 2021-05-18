using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using UnityEngine;
#if ENABLE_WINMD_SUPPORT
using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
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
    public string videoServiceName = "50002";
    public uint videoWidth = 960;
    public uint videoHeight = 540;
    public double frameRate = 15.0;
#if ENABLE_WINMD_SUPPORT
    private MediaCapture mediaCapture;
    private MediaFrameReader audioFrameReader;
    private StreamSocketListener audioSocketListener;
    private IOutputStream audioStream;
    private MediaFrameReader videoFrameReader;
    private StreamSocketListener videoSocketListener;
    private IOutputStream videoStream;

    async Task Start()
    {
        // Socket listener
        audioSocketListener = new StreamSocketListener();
        audioSocketListener.ConnectionReceived += OnConnectionAudio;
        await audioSocketListener.BindServiceNameAsync(audioServiceName);
        videoSocketListener = new StreamSocketListener();
        videoSocketListener.ConnectionReceived += OnConnectionVideo;
        await videoSocketListener.BindServiceNameAsync(videoServiceName);

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
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            RecordMediaDescription = selectedDescription,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
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
        var videoFrameSources = mediaCapture.FrameSources.Where(src => src.Value.Info.SourceKind == MediaFrameSourceKind.Color);
        if (videoFrameSources.Count() == 0)
        {
            Debug.Log("No video source was found.");
            return;
        }
        // MediaFrameSource videoFrameSource = videoFrameSources.FirstOrDefault().Value;
        MediaFrameSource videoFrameSource = null;
        MediaFrameFormat selectedFormat = null;
        foreach (var kv in videoFrameSources)
        {
            MediaFrameSource source = kv.Value;
            foreach (MediaFrameFormat format in source.SupportedFormats)
            {
                if (format.VideoFormat.Width == videoWidth && format.VideoFormat.Height == videoHeight
                    && format.FrameRate.Numerator == frameRate && format.FrameRate.Denominator == 1)
                {
                    videoFrameSource = source;
                    selectedFormat = format;
                    break;
                }
            }
            if (videoFrameSource != null)
            {
                break;
            }
        }
        if (selectedFormat != null)
        {
            await videoFrameSource.SetFormatAsync(selectedFormat);
        }
        else
        {
            Debug.Log("Cannot find a proper MediaFrameFormat.");
            return;
        }

        // Start streaming
        audioFrameReader = await mediaCapture.CreateFrameReaderAsync(audioFrameSource);
        audioFrameReader.FrameArrived += AudioFrameArrived;
        videoFrameReader = await mediaCapture.CreateFrameReaderAsync(videoFrameSource);
        videoFrameReader.FrameArrived += VideoFrameArrived;

        var audioStartStatus = audioFrameReader.StartAsync();
        var videoStartStatus = videoFrameReader.StartAsync();
        if (await audioStartStatus != MediaFrameReaderStartStatus.Success)
        {
            Debug.Log("The audioFrameReader couldn't start.");
        }
        if (await videoStartStatus != MediaFrameReaderStartStatus.Success)
        {
            Debug.Log("The videoFrameReader couldn't start.");
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
            if (reference != null && audioStream != null)
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

    private async void OnConnectionVideo(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var streamSocket = args.Socket;
        videoStream = streamSocket.OutputStream;
        Debug.Log("Received Video Connection.");
    }

    private void VideoFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using (MediaFrameReference reference = sender.TryAcquireLatestFrame())
        {
            if (reference != null && videoStream != null)
            {
                ProcessVideoFrame(reference.VideoMediaFrame);
            }
        }
    }

    unsafe private void ProcessVideoFrame(VideoMediaFrame videoMediaFrame)
    {
        float focalX = videoMediaFrame.CameraIntrinsics.FocalLength.X;
        float focalY = videoMediaFrame.CameraIntrinsics.FocalLength.Y;
        uint imageWidth = videoMediaFrame.CameraIntrinsics.ImageWidth;
        uint imageHeight = videoMediaFrame.CameraIntrinsics.ImageHeight;

        SoftwareBitmap softwareBitmap = videoMediaFrame.SoftwareBitmap;
        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Nv12)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Nv12);
        }
        BitmapBuffer bitmapBuffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Read);
        IMemoryBufferReference reference = bitmapBuffer.CreateReference();

        byte[] buf = new byte[4 * 4 + imageWidth * imageHeight * 3 / 2];
        fixed (byte* pBufByte = buf)
        {
            float* pBufFloat = (float*)pBufByte; 
            uint* pBufUint = (uint*)pBufByte;
            byte* dataInBytes;
            uint capacityInBytes;
            ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);

            pBufFloat[0] = focalX;
            pBufFloat[1] = focalY;
            pBufUint[2] = imageWidth;
            pBufUint[3] = imageHeight;
            for (uint i = 0; i < imageWidth * imageHeight * 3 / 2; i++)
            {
                pBufByte[4 * 4 + i] = dataInBytes[i];
            }
        }

        videoStream.WriteAsync(buf.AsBuffer());
    }

    async Task OnDestroy()
    {
        await audioFrameReader.StopAsync();
        await videoFrameReader.StopAsync();

        audioFrameReader.FrameArrived -= AudioFrameArrived;
        audioStream.FlushAsync();
        audioStream.Dispose();
        audioStream = null;
        videoFrameReader.FrameArrived -= VideoFrameArrived;
        videoStream.FlushAsync();
        videoStream.Dispose();
        videoStream = null;

        mediaCapture.Dispose();
    }
#endif
}
