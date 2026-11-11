using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Widget;
using ZXing;
using ZXing.Common;
using AndroidResult = Android.App.Result;

namespace PalladiumWallet.Mobile;

/// <summary>
/// Full-screen activity for QR scanning via Camera2 + ZXing.Net.
/// Returns to MainActivity via SetResult with the extra "qr" = code text.
/// </summary>
[Activity(Theme = "@style/MyTheme.NoActionBar",
          ScreenOrientation = Android.Content.PM.ScreenOrientation.Portrait)]
internal sealed class ScannerActivity : Activity, TextureView.ISurfaceTextureListener
{
    internal const string ResultKey = "qr";
    private const int PermReq = 1;

    private HandlerThread? _bgThread;
    private Handler? _bgHandler;
    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private ImageReader? _imageReader;
    private SurfaceTexture? _surfaceTex;
    private bool _permOk;
    private volatile bool _done;
    private long _lastDecode;
    private TextView? _hint;
    // Animated QR (UR, e.g. a PSBT from an air-gapped signer): frames are collected until the message is whole.
    private readonly PalladiumWallet.Core.Wallet.Ur.UrDecoder _ur = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window!.SetFlags(WindowManagerFlags.Fullscreen, WindowManagerFlags.Fullscreen);

        var root = new FrameLayout(this);

        var preview = new TextureView(this) { SurfaceTextureListener = this };
        root.AddView(preview, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent));

        var hint = _hint = new TextView(this) { Text = "Inquadra un codice QR" };
        hint.SetTextColor(Color.White);
        hint.SetBackgroundColor(Color.ParseColor("#AA000000"));
        hint.SetPadding(24, 12, 24, 12);
        root.AddView(hint, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WrapContent, FrameLayout.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.CenterHorizontal) { TopMargin = 60 });

        var cancel = new Button(this) { Text = "Annulla" };
        cancel.Click += (_, _) => Finish();
        root.AddView(cancel, new FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.WrapContent, FrameLayout.LayoutParams.WrapContent,
            GravityFlags.Bottom | GravityFlags.CenterHorizontal) { BottomMargin = 80 });

        SetContentView(root);

        _bgThread = new HandlerThread("CamBg");
        _bgThread.Start();
        _bgHandler = new Handler(_bgThread.Looper!);

        if (CheckSelfPermission(Android.Manifest.Permission.Camera) == Android.Content.PM.Permission.Granted)
            _permOk = true;
        else
            RequestPermissions([Android.Manifest.Permission.Camera], PermReq);
    }

    public override void OnRequestPermissionsResult(int req, string[] perms, Android.Content.PM.Permission[] grants)
    {
        _permOk = grants.Length > 0 && grants[0] == Android.Content.PM.Permission.Granted;
        if (_permOk) TryOpen(); else Finish();
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture st, int w, int h) { _surfaceTex = st; TryOpen(); }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture st) { CloseCamera(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) { }
    public void OnSurfaceTextureUpdated(SurfaceTexture st) { }

    private void TryOpen() { if (_permOk && _surfaceTex != null) OpenCamera(_surfaceTex); }

    private void OpenCamera(SurfaceTexture st)
    {
        var mgr = (CameraManager)GetSystemService(CameraService)!;

        var ids = mgr.GetCameraIdList()!;
        string cameraId = ids[0];
        foreach (var id in ids)
        {
            var chars = mgr.GetCameraCharacteristics(id)!;
            if (chars.Get(CameraCharacteristics.LensFacing) is Java.Lang.Integer f
                && f.IntValue() == (int)LensFacing.Back)
            { cameraId = id; break; }
        }

        var map = mgr.GetCameraCharacteristics(cameraId)!
                     .Get(CameraCharacteristics.ScalerStreamConfigurationMap)
                  as Android.Hardware.Camera2.Params.StreamConfigurationMap;
        var allSizes = map!.GetOutputSizes((int)ImageFormatType.Jpeg)!;
        var size = allSizes.OrderBy(s => Math.Abs(s.Width - 640)).First();

        st.SetDefaultBufferSize(size.Width, size.Height);
        _imageReader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Jpeg, 2);
        _imageReader.SetOnImageAvailableListener(new ImageListener(this), _bgHandler);

        mgr.OpenCamera(cameraId, new OpenCb(this, st), _bgHandler);
    }

    private void CloseCamera()
    {
        _session?.Close();  _session = null;
        _camera?.Close();   _camera  = null;
        _imageReader?.Close(); _imageReader = null;
        _bgThread?.QuitSafely();
    }

    internal void OnCameraOpened(CameraDevice dev, SurfaceTexture st)
    {
        _camera = dev;
        var prevSurf = new Surface(st);
        var capSurf  = _imageReader!.Surface!;
#pragma warning disable CA1422 // CreateCaptureSession(IList,...) obsoleted on API 30; replacement needs API 28, our min is 23
        dev.CreateCaptureSession([prevSurf, capSurf], new SessionCb(this, prevSurf, capSurf), _bgHandler);
#pragma warning restore CA1422
    }

    internal void OnSessionReady(CameraCaptureSession session, Surface prev, Surface cap)
    {
        _session = session;
        var req = _camera!.CreateCaptureRequest(CameraTemplate.Preview);
        req.AddTarget(prev);
        req.AddTarget(cap);
        session.SetRepeatingRequest(req.Build()!, null, _bgHandler);
    }

    internal void TryDecode(ImageReader reader)
    {
        if (_done) return;
        var image = reader.AcquireLatestImage();
        if (image == null) return;
        try
        {
            var now = SystemClock.ElapsedRealtime();
            if (now - _lastDecode < 400) return; // max ~2.5 fps
            _lastDecode = now;

            var buf = image.GetPlanes()![0].Buffer!;
            var bytes = new byte[buf.Remaining()];
            buf.Get(bytes);

            var bmp = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (bmp == null) return;
            int w = bmp.Width, h = bmp.Height;
            var pixels = new int[w * h];
            bmp.GetPixels(pixels, 0, w, 0, 0, w, h);
            bmp.Recycle();

            // ARGB int[] → RGB byte[] for ZXing.RGBLuminanceSource
            var rgb = new byte[pixels.Length * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgb[i * 3]     = (byte)(pixels[i] >> 16);
                rgb[i * 3 + 1] = (byte)(pixels[i] >> 8);
                rgb[i * 3 + 2] = (byte) pixels[i];
            }

            var source = new RGBLuminanceSource(rgb, w, h, RGBLuminanceSource.BitmapFormat.RGB24);
            var zreader = new BarcodeReaderGeneric { AutoRotate = true };
            zreader.Options.PossibleFormats = [BarcodeFormat.QR_CODE];
            zreader.Options.TryHarder = true;
            var result = zreader.Decode(source);
            if (result == null) return;

            var text = result.Text;
            if (text.StartsWith("ur:", StringComparison.OrdinalIgnoreCase))
            {
                if (!_ur.Receive(text)) return;
                if (!_ur.IsComplete)
                {
                    var pct = (int)(_ur.Progress * 100);
                    RunOnUiThread(() => _hint!.Text = $"QR animato: {pct}%");
                    return;
                }
                // A PSBT UR is handed back as base64, which the PSBT box already accepts.
                if (_ur.Psbt is not { } psbt) return;
                text = Convert.ToBase64String(psbt);
            }

            _done = true;
            RunOnUiThread(() =>
            {
                var intent = new Intent().PutExtra(ResultKey, text);
                SetResult(AndroidResult.Ok, intent);
                Finish();
            });
        }
        finally { image.Close(); }
    }

    protected override void OnDestroy()
    {
        CloseCamera();
        base.OnDestroy();
    }

    // ── Callback helpers ─────────────────────────────────────────────────

    private sealed class OpenCb(ScannerActivity a, SurfaceTexture st) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice cam) => a.OnCameraOpened(cam, st);
        public override void OnDisconnected(CameraDevice cam) { cam.Close(); a.Finish(); }
        public override void OnError(CameraDevice cam, CameraError err) { cam.Close(); a.Finish(); }
    }

    private sealed class SessionCb(ScannerActivity a, Surface prev, Surface cap) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession s) => a.OnSessionReady(s, prev, cap);
        public override void OnConfigureFailed(CameraCaptureSession s) => a.Finish();
    }

    private sealed class ImageListener(ScannerActivity a) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        // Interface declares ImageReader? but reader is never null in practice
        public void OnImageAvailable(ImageReader? reader) => a.TryDecode(reader!);
    }
}
