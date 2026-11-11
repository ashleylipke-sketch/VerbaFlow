import AVFoundation

/// The Mac's microphone (the one chosen in System Settings, Sound) as 16 kHz 16-bit mono sound, the form VerbaFlow's
/// live connection takes. It is on only while the person speaks.
final class Microphone {
    private let engine = AVAudioEngine()
    private var converter: AVAudioConverter?
    private let output = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!
    /// New sound, on the microphone's own thread.
    var sound: ((Data) -> Void)?
    /// How loud the newest sound was, 0 to 1, on the microphone's own thread.
    var level: ((Float) -> Void)?
    private(set) var isOn = false

    static var answered: Bool { AVCaptureDevice.authorizationStatus(for: .audio) != .notDetermined }
    static var permitted: Bool { AVCaptureDevice.authorizationStatus(for: .audio) == .authorized }

    /// Asks for the microphone if the person has not answered yet. True when it may be used.
    static func allowed() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    func start() throws {
        guard !isOn else { return }
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else { throw PlainError(message: "No microphone was found. Check System Settings, Sound, Input.") }
        converter = AVAudioConverter(from: format, to: output)
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in self?.convert(buffer) }
        engine.prepare()
        do { try engine.start() } catch {
            input.removeTap(onBus: 0)
            throw PlainError(message: "The microphone could not be started. Check System Settings, Sound, Input, then try again.")
        }
        isOn = true
    }

    func stop() {
        guard isOn else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        isOn = false
    }

    private func convert(_ buffer: AVAudioPCMBuffer) {
        guard let converter else { return }
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * output.sampleRate / buffer.format.sampleRate) + 32
        guard let out = AVAudioPCMBuffer(pcmFormat: output, frameCapacity: capacity) else { return }
        var given = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if given { status.pointee = .noDataNow; return nil }
            given = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, out.frameLength > 0, let samples = out.int16ChannelData else { return }
        let n = Int(out.frameLength)
        var peak: Int32 = 0
        for i in 0..<n { peak = max(peak, abs(Int32(samples[0][i]))) }
        level?(Float(peak) / 32768)
        sound?(Data(bytes: samples[0], count: n * 2))
    }
}
