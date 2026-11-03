import Foundation
import VerbaFlowShared

/// One listen: the microphone's sound goes to VerbaFlow over a live connection and the text comes back, exactly as in
/// Speech mode and the other Widgets. A lost connection is made again (after 1, 2, 4, 8, then every 10 seconds) and the
/// sound since the end of the last finished phrase is sent again, so nothing is missed or doubled. The sound is held only
/// in memory while listening and is dropped when the listen ends.
final class LiveListener {
    struct Heard { let segments: [LivePara]; let pendingWords: Int; let startsWithBreak: Bool }

    /// Called on the main thread.
    var heard: (@MainActor (Heard) -> Void)?
    var link: (@MainActor (Bool) -> Void)?
    var failed: (@MainActor (String) -> Void)?
    /// The listen ended: true when every word came back, with the last text heard (the main thread may not have seen it yet).
    var ended: (@MainActor (Bool, Heard?) -> Void)?

    private static let bytesPerMs = 32 // 16 000 samples a second, two bytes each
    private let api: Api
    private let liveId: String
    private var ticket: String?
    private let q = DispatchQueue(label: "net.verbaflow.live")
    private var audio = Data()
    private var finalToMs = 0, connections = 0, attempt = 0, generation = 0
    private(set) var reconnects = 0
    private var socket: URLSessionWebSocketTask?
    private var lastHeard: Heard?
    private var ready = false, stopping = false, finished = false, failedNow = false, reported = false

    init(api: Api, start: WidgetStart) {
        self.api = api
        liveId = start.liveId
        ticket = start.ticket
    }

    var spokenMs: Int { q.sync { audio.count / Self.bytesPerMs } }

    func start() { q.async { self.connect() } }

    /// Sound from the microphone, 16 kHz 16-bit mono.
    func write(_ pcm: Data) {
        q.async {
            guard !self.stopping, !self.reported else { return }
            self.audio.append(pcm)
            if self.ready { self.socket?.send(.data(pcm)) { _ in } }
        }
    }

    /// The person stopped speaking: the last words are finished and sent back, then `ended` is called.
    func stop() {
        q.async {
            guard !self.stopping else { return }
            self.stopping = true
            if self.finished || self.failedNow { self.report(self.finished); return }
            if self.ready { self.sendEnd() }
            // Without a connection the last words cannot come back; the words heard so far are kept.
            self.q.asyncAfter(deadline: .now() + 15) { self.report(false) }
        }
    }

    /// Ends the listen at once and drops the sound held.
    func close() { q.async { self.reported = true; self.drop() } }

    private func drop() {
        generation += 1
        socket?.cancel(with: .goingAway, reason: nil)
        socket = nil
        ready = false
        audio = Data()
    }

    private func report(_ complete: Bool) {
        guard !reported else { return }
        reported = true
        drop()
        let ended = self.ended, last = lastHeard
        Task { @MainActor in ended?(complete, last) }
    }

    private func sendEnd() { socket?.send(.string("{\"type\":\"end\"}")) { _ in } }

    private func connect() {
        guard !reported else { return }
        if let t = ticket { ticket = nil; open(t); return }
        Task {
            do {
                let t = try await api.ticket(liveId)
                q.async { self.open(t) }
            } catch { q.async { self.lost() } }
        }
    }

    private func open(_ ticket: String) {
        guard !reported else { return }
        generation += 1
        let gen = generation
        let from = finalToMs
        let ws = URLSession.shared.webSocketTask(with: api.socketURL(liveId, ticket: ticket, fromMs: from))
        socket = ws
        if connections > 0 { reconnects += 1 }
        connections += 1
        ws.resume()
        receive(ws, gen, from)
    }

    private func receive(_ ws: URLSessionWebSocketTask, _ gen: Int, _ from: Int) {
        ws.receive { result in
            self.q.async {
                guard gen == self.generation else { return }
                switch result {
                case .failure: self.lost()
                case .success(let message):
                    let data: Data?
                    switch message {
                    case .string(let s): data = s.data(using: .utf8)
                    case .data(let d): data = d
                    @unknown default: data = nil
                    }
                    if let data, let m = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] { self.handle(m, from) }
                    if gen == self.generation, !self.reported { self.receive(ws, gen, from) }
                }
            }
        }
    }

    private func handle(_ m: [String: Any], _ from: Int) {
        switch m["type"] as? String {
        case "ready":
            ready = true
            attempt = 0
            let link = self.link
            Task { @MainActor in link?(true) }
            // The sound since the end of the last finished phrase goes again, in pieces of a second.
            var at = min(audio.count, from * Self.bytesPerMs)
            while at < audio.count {
                let end = min(audio.count, at + 1000 * Self.bytesPerMs)
                socket?.send(.data(audio.subdata(in: at..<end))) { _ in }
                at = end
            }
            if stopping { sendEnd() }
        case "text":
            if let f = m["finalToMs"] as? Int { finalToMs = f }
            publish(m)
        case "done":
            finished = true
            if let f = m["finalToMs"] as? Int { finalToMs = f }
            publish(m)
            report(true)
        case "error":
            failedNow = true
            let message = m["message"] as? String ?? "The speech service stopped. The words heard so far are kept."
            let failed = self.failed
            Task { @MainActor in failed?(message) }
            if stopping { report(false) } else { drop() }
        default: break
        }
    }

    private func publish(_ m: [String: Any]) {
        let segs = (m["segments"] as? [[String: Any]] ?? []).map {
            LivePara(text: $0["text"] as? String, newParagraph: $0["newParagraph"] as? Bool, list: $0["list"] as? String)
        }
        let h = Heard(segments: segs, pendingWords: m["pendingWords"] as? Int ?? 0, startsWithBreak: m["startsWithBreak"] as? Bool ?? false)
        lastHeard = h
        let heard = self.heard
        Task { @MainActor in heard?(h) }
    }

    private func lost() {
        ready = false
        socket = nil
        guard !finished, !failedNow, !reported else { return }
        let link = self.link
        Task { @MainActor in link?(false) }
        let wait = Double(min(10, 1 << min(attempt, 4)))
        attempt += 1
        q.asyncAfter(deadline: .now() + wait) { self.connect() }
    }
}
