# Clash Solution — C# Revit add-in

Chuyển chức năng của pyRevit Clash Solution v0.8.2 sang project C# đã tạo. UI WPF sử dụng màu sắc, style card, nút, checkbox, bảng và title bar từ `FamilyWizardWindow.xaml`.

## Chức năng

- Chọn model hiện tại hoặc từng instance Revit Link đã load cho Set A/B.
- Chọn nhiều category, tìm kiếm category, preset Common MEP và Common A/S.
- Entire Model, Active View hoặc nhiều host-model level. Host dùng level được gán cho element; link dùng dải cao độ level trong tọa độ host.
- Nhiều rule parameter instance/type; Equals, Contains, Starts with, Not Equals; AND/OR; dropdown giá trị thực và nhập tay.
- Lọc bounding box bằng spatial grid, sau đó giao Solid bằng Revit Boolean API. Link geometry được transform về host. Ngưỡng thể tích mặc định 0.10 cm³ và có thể chỉnh trong Clash Conditions.
- Bảng ID, source, category, type, level, System/Classification/Service. ID link là ID element trong model link.
- Select + Zoom (link chọn link instance trên host), Section Box 2,000 mm ở view 3D hiện tại.
- Xuất CSV UTF-8 và XLSX có header, màu xen kẽ, freeze header, autofilter; không cần cài Excel hoặc thư viện NuGet.
- Profile New / Save / Save As / Delete; đọc được schema và legacy profile của Python tại `%APPDATA%\ClashSolution\profiles.json`. Lưu có backup `.bak`; nếu JSON lỗi, không ghi đè file cũ.
- Cửa sổ modeless; thao tác Revit chạy qua ExternalEvent. Tính giao được chia đợt để cập nhật tiến độ và hủy, giữ kết quả một phần khi hủy. Đổi/đóng model hoặc sửa model trong lúc chạy sẽ hủy; sửa geometry sau khi chạy sẽ xóa kết quả cũ. Thu thập phần tử và dựng candidate list ở đầu mỗi lần chạy vẫn chạy đồng bộ.

Active View của link giữ cách lọc của Python: visibility của link instance và bounding box crop/section box. Đây không phải kiểm tra toàn bộ override hiển thị từng element bên trong link. Geometry Revit không tính được được báo trong số geometry warnings.

## Build và nạp vào Revit

Yêu cầu: Visual Studio / Build Tools với .NET desktop development và .NET Framework 4.8 targeting pack cho Revit 2023/2024; .NET SDK có .NET 8 Windows Desktop targeting pack cho Revit 2025. Reference API lấy từ đúng bản Revit cài trên máy; không copy RevitAPI DLL vào output.

Chạy PowerShell tại thư mục project chứa file `Build.ps1`:

```powershell
.\Build.ps1 -RevitVersion 2023
.\Build.ps1 -RevitVersion 2024
.\Build.ps1 -RevitVersion 2025
```

Output: `bin\<năm>\Release\MEP_Check_Clash_ver1.dll` và `ClashSolution.addin`. Các bản dùng thư mục `obj` riêng. Revit 2024/2025 dùng ElementId 64-bit. Project gốc dùng .NET Framework 4.8; project `MEP_Check_Clash_ver1.Revit2025.csproj` dùng .NET 8. Solution chứa cả hai project.

Để cài manifest cho đúng phiên bản:

```powershell
.\Build.ps1 -RevitVersion 2023 -Install
# Hoặc 2024 / 2025
```

`-Install` copy manifest vào `%APPDATA%\Autodesk\Revit\Addins\<năm>\ClashSolution.CSharp.addin`. Manifest trỏ tới DLL trong thư mục build, vì vậy cần giữ nguyên vị trí thư mục. Khởi động lại Revit, mở model, chọn **Clash Solution → Clash Detection → Clash Detector**. Nếu đường dẫn Revit khác mặc định, truyền `-RevitInstallDir 'D:\Autodesk\Revit 2023'`. Folder Python vẫn được giữ riêng.

Chỉ build và tạo manifest chưa cài add-in vào Revit. Không dùng DLL 2023/2024 cho Revit 2025.

## Kiểm tra

- Đã build Release với API cài trên máy cho Revit 2023, 2024 và 2025.
- `tools\Verify-Core.ps1`: kiểm tra import profile Python, roundtrip và backup, legacy schema, giữ nguyên JSON lỗi, rule AND/OR, Unicode/escaping CSV và cấu trúc XLSX.
- `tools\Render-Preview.ps1`: render XAML thật ngoài màn hình, dùng dữ liệu minh họa để kiểm tra bố cục ở 1700 và 1360 px; ảnh trong `artifacts`.
- Geometry Boolean, link transform, Active View/level scope, ExternalEvent/cancel, zoom và transaction Section Box cần kiểm thử trong Revit với model thực tế. Build và preview không xác nhận các thao tác này đã chạy thành công trong Revit.

Chạy các script kiểm tra bằng Windows PowerShell 5.1; render cần STA:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Verify-Core.ps1
powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\tools\Render-Preview.ps1
```

Lỗi thao tác được hiện ở thanh trạng thái; chi tiết ghi tại `%LOCALAPPDATA%\ClashSolution\errors.log`.

## Bản sửa 1.0.2

- So sánh document bằng `Document.Equals()` thay vì so sánh reference, ở guard, link validation và callback thay đổi model.
- Cache Solid dùng bản clone do tool sở hữu, tách khỏi GeometryElement của model/instance trước khi trả quyền điều khiển cho Revit.
- Hiện phiên bản DLL trên cửa sổ; log có PID, phiên bản, bước thu thập/candidate/geometry và exception. Callback UI chỉ xử lý exception có stack thuộc cửa sổ của tool; không chặn lỗi native hoặc lỗi từ add-in khác. Quan sát exception .NET dẫn đến termination chỉ ghi log, không ngăn CLR kết thúc process.
- Journal 0262 ghi crash lúc 13:56 ngày 08/10/2026 với mã `0xe0434352`. DLL Addin Manager nạp trong phiên đó được xác nhận là 1.0.0; các lần Run trước crash bị guard chặn. Dump/journal hiện có chưa xác định được exception cụ thể gây crash. Không coi build thành công là xác nhận đã xử lý hoàn toàn crash.

Đóng/mở lại Revit rồi nạp DLL mới. Kiểm tra cửa sổ hiện **v1.0.2.0**, vì Addin Manager có thể giữ assembly cũ trong phiên Revit hiện tại. Bản mới cần kiểm thử tiếp trong Revit với model thực tế.

## Giao diện 1.1.0

- Thiết lập ở bên trái, kết quả và 3D ở bên phải; có splitter điều chỉnh bề rộng.
- Chọn nhiều level bằng dropdown checkbox. `Active View Level` là tùy chọn trong dropdown, tự lấy level của active view tại thời điểm chạy. Chọn tùy chọn này sẽ bỏ các level cụ thể và ngược lại. Các profile level cũ vẫn được áp dụng; profile mới có thể lưu scope `active_view_level` của bản C#.
- Bảng chỉ còn No., ID A, Category A, ID B, Category B. System/Service có thể đưa vào export bằng checkbox riêng.
- Chọn kết quả để xem mesh 3D của hai element. Preview WPF có màu A/B, kéo chuột để orbit, lăn chuột để zoom; tối đa 30.000 tam giác mỗi element. Link geometry được đưa về tọa độ host. Đây là preview hai phần tử, không chứa toàn bộ context model.
- Chọn view 3D orthographic từ dropdown rồi bấm `View clash`: mở view trong Revit, tạo section box 2.000 mm quanh clash, chọn phần tử/host link và zoom đến đó. Có tùy chọn tạo view 3D mới. Việc này thay đổi section box của view đã chọn, có thể Undo trong Revit.
- Preview dùng WPF mesh, không dùng Revit PreviewControl vì Autodesk yêu cầu host của control đó là modal trong khi tool này là modeless.
- Phiên bản hiện tại: **v1.1.0.0**. Build và QA layout không thay thế kiểm thử geometry/điều hướng trong Revit.

## Logo, geometry, portable profile và điều kiện clash 1.2.0

- Logo trong `Assets/ClashLogo.png` bỏ toàn bộ tên, giữ biểu tượng ống/kết cấu/cảnh báo, nền trong suốt. Asset được embed vào DLL để dùng cho ribbon, icon cửa sổ và title bar, không cần copy PNG khi cài add-in. Logo được sửa bằng imagegen với yêu cầu giữ hình và màu của mẫu, xóa chữ/card/nền và xuất icon PNG trong suốt.
- Category chỉ lấy các nhóm có Solid; không đưa hệ thống MEP, group, Revit link instance, room/space và assembly vào đối tượng kiểm tra. Các category MEP vật lý chuẩn luôn có trong danh sách, kể cả nguồn chưa có instance, bao gồm Cable Trays và Cable Tray Fittings. Danh sách vẫn chịu ảnh hưởng của ô tìm kiếm category; category chưa có instance sẽ không tạo kết quả.
- Profile `Export` xuất thiết lập hiện tại thành JSON. `Import` đọc JSON, lưu profile vào kho per-user và áp dụng profile đầu tiên. Giữ scope/levels (bao gồm Active View Level), nguồn và category A/B, AND/OR và rules của từng set, geometry options và Clash Conditions. Source/category không có trong model hiện tại được báo thiếu. Tên trùng được đổi thành `Name (imported 1)` thay vì ghi đè profile cũ. Profile Python cũ được đọc với điều kiện clash mặc định.
- Parameter Filters — Set A và Set B được giữ riêng: hai nhóm có thể là hai model/hệ/loại phần tử khác nhau và cần rule khác nhau. AND/OR chỉ áp dụng trong từng nhóm; rules không áp dụng lẫn sang nhóm còn lại.
- Hard clash phải có giao Solid thể tích dương. Face-touching không được coi là clash. `Min overlap volume (cm³)` áp dụng cho tổng thể tích giao dương của các cặp Solid giữa hai element, và kết quả phải **lớn hơn** ngưỡng. Các phần giao nhỏ được cộng trước khi xét ngưỡng.
- `Min intersection box size (mm)` yêu cầu cả ba kích thước X/Y/Z của bounding box bao quanh phần giao trong tọa độ host đạt ngưỡng; 0 tắt điều kiện này. Đây là điều kiện kích thước hộp phần giao, không phải độ xuyên, khoảng cách clearance hay phép đo khoảng cách chính xác giữa hai vật thể.
- `Ignore directly connected MEP pairs` bỏ cặp nối vật lý trực tiếp qua connector trong cùng source. Mặc định tắt. Không bỏ cặp chỉ vì cùng System Type.
- Kiểm tra tiêu chí không âm/hữu hạn, strict volume boundary, box boundary, zero-volume contact và lưu/import toàn bộ criteria đã pass trong `tools/Verify-Core.ps1`. Build Release 2023/2024/2025 và QA XAML đã pass. Phát hiện geometry và nối connector thực tế vẫn cần kiểm thử trong Revit.
- Phiên bản hiện tại: **v1.3.2.0**. Khởi động lại Revit trước khi nạp DLL mới khi dùng Addin Manager.
- Bố cục v1.3.2: All/Clear thay Common ở cả hai set; All chọn toàn bộ category geometry trong set, kể cả category đang bị ẩn bởi ô tìm kiếm. Toolbar Parameter Filters trên một hàng; ô màu nằm cạnh mã màu; Conditions/Colors cạnh nhau. Refresh sources và Run Hard Clash cùng chiều cao 38; bảng có đường phân cách giữa hai nhóm A/B.
- Bố cục v1.3.1: Set A/B ngay dưới Clash Profile; Clash Colors bên trái dưới Clash Conditions; Run Hard Clash cạnh Refresh sources, ngoài vùng cuộn và các khung thiết lập.

Clash Colors (v1.3.0):
- Chọn màu riêng cho A/B hoặc nhập mã #RRGGBB; mặc định A xanh lá #32CD32, B đỏ #FF3333. Preview 3D dùng màu đã chọn cho cả host và link. Profile Save/Export/Import giữ màu và tùy chọn tự tô khi View clash; profile cũ nhận màu mặc định.
- `Color selected` tô hai phần tử của clash đang chọn trong active view. `Color all results` tô các phần tử thuộc tất cả kết quả. Mỗi phần tử chỉ được xử lý một lần; nếu nằm ở cả A và B, màu B được ưu tiên.
- `Apply colors when opening View clash` tự tô cặp đang chọn trong view 3D được mở. Overrides áp dụng cho line, solid surface/cut foreground fill, tắt halftone và transparency của các phần tử đó trong view.
- `Restore colors` phục hồi toàn bộ element overrides ban đầu của những phần tử tool đã tô trong active view, trong phiên cửa sổ tool hiện tại. Nếu đóng tool rồi mở lại, dùng Undo Revit hoặc quản lý Overrides của view để phục hồi. Thao tác tô và phục hồi đều là transaction có thể Undo.
- Chỉ tô trực tiếp phần tử thuộc current model. Phần tử trong Revit Link hiển thị màu ở preview; tool không tô cả link hoặc sửa file link. Số phần tử link bị bỏ qua được báo trong status.
- Build 2023/2024/2025, profile color defaults/roundtrip và render XAML đã kiểm tra; override màu thực tế cần kiểm tra trong Revit.
