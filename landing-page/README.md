# Night Tales Studio — Landing Page

Trang Landing Page chính thức cho **Night Tales Studio** (Hệ thống phòng thu bán tự động cho nhà sáng tạo nội dung YouTube).

---

## 1. Cấu trúc File

```
landing-page/
├── index.html        # File landing page độc lập (HTML5 + CSS3 + JS nhúng hoàn chỉnh, không CDN ngoài)
└── README.md         # Tài liệu hướng dẫn sử dụng, thông số responsive và tokens thiết kế
```

*File cũng được đồng bộ dự phòng tại:*
- `design/landing-page/index.html`
- `design/ui/screens/landing-page.html`

---

## 2. Đặc điểm Nổi bật

1. **Hoàn toàn Độc lập (Self-contained)**:
   - Không phụ thuộc vào CDN bên ngoài (Bootstrap, Tailwind, Google Fonts cdn,...).
   - Tự động chạy mượt mà trên môi trường offline hoặc deploy tĩnh (Static hosting, S3, SeaweedFS, Nginx).
2. **Hệ thống Design Tokens (Dark Control Room)**:
   - Màu nền chính: `#202523` (Canvas room) / `#2A312E` (Soft surface).
   - Màu tín hiệu: `#6ECAC0` (Signal Teal) / `#FF786C` (Coral Playhead).
   - Font chữ chuẩn: `Be Vietnam Pro` (Fallback `system-ui`, `-apple-system`).
3. **Responsive Đa thiết bị (Multi-Device Adaptiveness)**:
   - **Desktop (>= 1200px)**: Bố cục 2 cột rộng rãi, thanh Stepper 6 trạm thẳng hàng, Split-panel tương tác (Bản thảo vs Phân cảnh).
   - **Tablet & Laptop (768px – 1024px)**: Hero chuyển xếp tầng, Stepper 6 trạm dàn 3 cột × 2 dòng.
   - **Mobile (360px – 768px)**: Thanh điều hướng rút gọn vào mobile drawer mờ đục, các nút đạt chuẩn cảm ứng tối thiểu 44px.
   - **Màn hình nhỏ (<= 360px)**: Cơ chế chống tràn viền Zero-Overflow, tự động ẩn telemetry và tinh chỉnh padding.
4. **Cơ chế Tự động Viết tắt Tiêu đề (Responsive Title Abbreviation)**:
   - **Header Brand Title**:
     - Desktop / Tablet (`> 640px`): `Night Tales Studio` + `Phòng thu Kể chuyện Đêm`.
     - Mobile (`<= 640px`): `Night Tales`.
     - Màn hình nhỏ (`<= 360px`): `NTS`.
   - **Trạm 03 Console Title**:
     - Desktop (`> 480px`): `Trạm 03: Đang duyệt kịch bản` (EN: `Station 03: Script Approval`).
     - Mobile (`<= 480px`): `Trạm 03: Duyệt kịch bản` (EN: `Station 03: Script Review`).
     - Màn hình nhỏ (`<= 360px`): `T03: Duyệt kịch bản` (EN: `St. 03: Review`).
5. **Hỗ trợ Song ngữ Toàn diện (Bilingual VI / EN)**:
   - Chuyển đổi ngôn ngữ tức thì qua nút `VI / EN` trên header.
   - Tự động chuyển đổi định dạng tiền tệ (`₫` VNĐ sang `$ USD`).
6. **Mô phỏng Phòng thu Trạm 3 (Interactive Station 3)**:
   - Nút **"Bật mô phỏng tua sóng âm"** cho phép chạy playhead thời gian thực dọc theo bảng cue sheet.
   - Thao tác nhấp chọn từng thẻ phân cảnh để đổi vị trí playhead.
7. **Modal "Coming Soon"**:
   - Tất cả các nút "Tạo tài khoản" và "Đăng nhập" kích hoạt popup thông báo dự án đang hoàn thiện với form đăng ký nhận tin sớm.
   - Hỗ trợ phím tắt `Escape` và bấm ra ngoài để đóng nhanh.
8. **Thanh điều hướng Cố định & Bộ nút Lướt trang (Sticky Nav & Studio Elevator)**:
   - Header cố định (`position: sticky`, `top: 0`, `z-index: 1000`) với hiệu ứng làm mờ nền kính mờ (`backdrop-filter: blur(16px)`) và viền ánh sáng khi cuộn (`.header-scrolled`). Khắc phục triệt để lỗi mất sticky trên WebKit/Safari bằng thuộc tính `overflow-x: clip`.
   - Cụm điều hướng nhanh nổi (Floating Studio Elevator) góc dưới bên phải gồm nút Cuộn lên đỉnh (▲ Top) và Cuộn xuống đáy trang (▼ Bottom). Tự động hiện sau 120px và làm mờ nút khi đã ở cực đỉnh/đáy.
   - Nút nhảy nhanh Top/Bottom ngay trong Mobile Drawer để chuyển hướng tức thì khi mở menu di động.

---

## 3. Cách Mở & Xem trước (Preview)

### Cách 1: Mở trực tiếp bằng trình duyệt
```bash
open landing-page/index.html
```

### Cách 2: Khởi chạy HTTP Server cục bộ
```bash
# Sử dụng Python
python3 -m http.server 8080 --directory landing-page

# Hoặc sử dụng Node
npx serve landing-page
```
Truy cập: `http://localhost:8080`

### Cách 3: Xem qua OpenDesign Local Daemon (nếu OpenDesign đang chạy)
```
http://127.0.0.1:63033/api/projects/night-tales-studio-landing-vi-en/raw/night-tales-studio-landing.html
```
