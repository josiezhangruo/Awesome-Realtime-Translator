import AppKit
import SwiftUI

/// 让一块区域可以拖动整个窗口。
///
/// 为什么不能只靠 `isMovableByWindowBackground`：那个属性只在「这一点上没有任何
/// 视图要处理鼠标」时才生效。字幕区被 ScrollView 占着，按下去的事件被它接走，
/// 永远到不了窗口那一层 —— 实际表现就是顶栏空白处能拖、字幕上拖不动。
///
/// 这里放一个真实的 NSView 直接调 `performDrag`，不依赖系统对背景拖动的判定，
/// 也就不会随系统版本变。
///
/// 用法是 `.overlay`，不是 `.background`：放在背景层时点不到它 —— SwiftUI 的
/// 宿主视图把文字这类内容画在自己的图层里，命中测试落在宿主视图上就停了，
/// 根本轮不到下面那层。盖在上面才拿得到。
///
/// `hitTest` 直接返回自己，是因为盖在上面还不够保险：SwiftUI 会对没有交互的
/// 子视图做命中穿透。这一层本来就只负责拖动，让它无条件接管最省事。
///
/// **不实现 `scrollWheel`** 是故意的：滚轮事件会顺着响应链交给外层的 NSScrollView，
/// 所以这块区域可拖的同时，滚动照常。
struct WindowDragHandle: NSViewRepresentable {
    final class DragView: NSView {
        /// 按下那一刻，鼠标在窗口内的位置。拖动时让窗口跟着走，使这个相对位置保持不变。
        /// 存窗口内坐标而不是累加位移，是为了不累积误差，也不会因为窗口在拖动过程中
        /// 移动而产生反馈。
        private var grabOffset: NSPoint?

        /// **不用 `performDrag(with:)`。**
        ///
        /// 实测它在这个面板上不起作用 —— 命中测试是通的（最小复现里这一层确实
        /// 被点中、`mouseDownCanMoveWindow` 也是 true），但窗口就是不动。
        /// 这个面板同时是 `.nonactivatingPanel`、`canBecomeKey = false`、
        /// `level = .screenSaver`，具体是哪一条让 performDrag 失效没有继续追，
        /// 因为自己算位移同样简单，而且不依赖任何系统行为。
        override func mouseDown(with event: NSEvent) {
            grabOffset = event.locationInWindow
        }

        override func mouseDragged(with event: NSEvent) {
            guard let window, let grabOffset else { return }
            let onScreen = window.convertPoint(toScreen: event.locationInWindow)
            window.setFrameOrigin(NSPoint(x: onScreen.x - grabOffset.x,
                                          y: onScreen.y - grabOffset.y))
        }

        override func mouseUp(with event: NSEvent) {
            grabOffset = nil
        }

        /// 这一层范围内的点击一律归我。
        override func hitTest(_ point: NSPoint) -> NSView? {
            bounds.contains(convert(point, from: superview)) ? self : nil
        }
    }

    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}
}
