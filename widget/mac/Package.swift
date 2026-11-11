// swift-tools-version:5.9
import PackageDescription

// The VerbaFlow Widget for Mac: a menu bar program that lets a person speak into any Mac program (Option+Shift+D).
// It shares how heard text becomes paragraphs with the iPhone keyboard (../iphone/Shared). make-app.sh turns the
// built program into "VerbaFlow Widget.app"; GitHub builds it on a Mac (.github/workflows/mac.yml).
let package = Package(
    name: "VerbaFlowWidgetMac",
    platforms: [.macOS(.v13)],
    dependencies: [.package(path: "../iphone/Shared")],
    targets: [
        .target(name: "WidgetMacCore", dependencies: [.product(name: "VerbaFlowShared", package: "Shared")]),
        .executableTarget(name: "VerbaFlowWidget", dependencies: [
            "WidgetMacCore", .product(name: "VerbaFlowShared", package: "Shared"),
        ]),
        .testTarget(name: "WidgetMacCoreTests", dependencies: ["WidgetMacCore"]),
    ]
)
