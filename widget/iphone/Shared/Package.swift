// swift-tools-version:5.9
import PackageDescription

// What the VerbaFlow iPhone app and its keyboard share: how heard text becomes paragraphs, and how the two talk.
let package = Package(
    name: "VerbaFlowShared",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [.library(name: "VerbaFlowShared", targets: ["VerbaFlowShared"])],
    targets: [
        .target(name: "VerbaFlowShared"),
        .testTarget(name: "VerbaFlowSharedTests", dependencies: ["VerbaFlowShared"]),
    ]
)
