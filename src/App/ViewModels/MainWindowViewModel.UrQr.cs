using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NBitcoin;
using PalladiumWallet.App.Localization;
using PalladiumWallet.App.Services;
using PalladiumWallet.Core.Chain;
using PalladiumWallet.Core.Wallet;
using PalladiumWallet.Core.Wallet.Ur;
using QRCoder;

namespace PalladiumWallet.App.ViewModels;

// Animated-QR (BC-UR) transport of a PSBT for air-gapped signers: show the frames, or scan them (Android).
public partial class MainWindowViewModel
{
    private const int UrFrameMs = 350;

    [ObservableProperty]
    private bool isUrQrOpen;

    [ObservableProperty]
    private Bitmap? urQrImage;

    [ObservableProperty]
    private string urQrCaption = "";

    private DispatcherTimer? _urTimer;
    private UrEncoder? _urEncoder;
    private int _urShown;

    /// <summary>True where a camera scanner exists (Android), so a signed PSBT can be scanned back.</summary>
    public bool CanScanPsbt => PlatformServices.ScanQrAsync is not null;

    [RelayCommand]
    private void ShowPendingPsbtQr() => ShowUrQr(PendingPsbtBase64);

    [RelayCommand]
    private void ShowSignedPsbtQr() => ShowUrQr(SignedPsbtBase64);

    private void ShowUrQr(string psbtBase64)
    {
        if (string.IsNullOrEmpty(psbtBase64))
            return;
        try
        {
            var psbt = PsbtFormat.Parse(psbtBase64, PalladiumNetworks.For(Net));
            _urEncoder = UrEncoder.ForPsbt(PsbtFormat.ToBinary(psbt));
        }
        catch (Exception)
        {
            StatusMessage = Loc.Tr("msg.psbt.invalid");
            return;
        }
        _urShown = 0;
        IsUrQrOpen = true;
        NextUrFrame();
        if (!_urEncoder.IsSinglePart)
        {
            _urTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UrFrameMs) };
            _urTimer.Tick -= OnUrTick;
            _urTimer.Tick += OnUrTick;
            _urTimer.Start();
        }
    }

    private void OnUrTick(object? sender, EventArgs e) => NextUrFrame();

    private void NextUrFrame()
    {
        if (_urEncoder is null)
            return;
        var frame = _urEncoder.NextPart();
        _urShown++;
        var previous = UrQrImage;
        UrQrImage = RenderFrame(frame);
        previous?.Dispose();
        UrQrCaption = _urEncoder.IsSinglePart
            ? Loc.Tr("psbt.urqr.single")
            : string.Format(Loc.Tr("psbt.urqr.multi"), (_urShown - 1) % _urEncoder.PartCount + 1, _urEncoder.PartCount);
    }

    private static Bitmap? RenderFrame(string frame)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            // Uppercase selects the QR alphanumeric mode, which packs a frame into a smaller, easier-to-scan code.
            using var data = generator.CreateQrCode(frame.ToUpperInvariant(), QRCodeGenerator.ECCLevel.L);
            return new Bitmap(new MemoryStream(new PngByteQRCode(data).GetGraphic(8)));
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void CloseUrQr()
    {
        _urTimer?.Stop();
        _urEncoder = null;
        IsUrQrOpen = false;
        var previous = UrQrImage;
        UrQrImage = null;
        previous?.Dispose();
    }

    /// <summary>Scans a PSBT shown by another device (plain QR or animated UR) into the PSBT box.</summary>
    [RelayCommand]
    private async Task ScanPsbtQr()
    {
        if (PlatformServices.ScanQrAsync is not { } scan)
            return;
        var raw = await scan();
        if (string.IsNullOrWhiteSpace(raw))
            return;
        PsbtInput = raw.Trim();   // the Android scanner hands a completed UR back as base64
    }
}
