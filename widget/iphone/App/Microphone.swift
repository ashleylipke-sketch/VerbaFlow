import AVFoundation

/// The iPhone's microphone as 16 kHz 16-bit mono sound, the form VerbaFlow's live connection takes. While it is on, the
/// app keeps running in the background (iPhone shows the orange microphone dot), which is what lets the keyboard start
/// listening again without opening the app.
final class Microphone {
    private let engine = AVAudioEngine()
    private var converter: AVAudioConverter?
    private let output = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16_000, channels: 1, interleaved: true)!
    /// New sound, on the microphone's own thread. Nil while nobody is listening: the sound is then dropped at once.
    var sound: ((Data) -> Void)?
    private(set) var isOn = false

    /// Asks for the microphone if the person has not answered yet. True when it may be used.
    static func allowed() async -> Bool {
        await withCheckedContinuation { done in
            AVAudioSession.sharedInstance().requestRecordPermission { done.resume(returning: $0) }
        }
    }

    func start() throws {
        guard !isOn else { return }
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playAndRecord, mode: .default, options: [.mixWithOthers, .defaultToSpeaker, .allowBluetooth])
        try session.setActive(true)
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        converter = AVAudioConverter(from: format, to: output)
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in self?.convert(buffer) }
        engine.prepare()
        try engine.start()
        isOn = true
    }

    func stop() {
        guard isOn else { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        isOn = false
    }

    private func convert(_ buffer: AVAudioPCMBuffer) {
        guard let sound, let converter else { return }
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
        sound(Data(bytes: samples[0], count: Int(out.frameLength) * 2))
    }
}
