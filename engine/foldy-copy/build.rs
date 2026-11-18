// C ABI에서 C# P/Invoke 코드를 생성한다 (설계서 3장: csbindgen).
// 결과 파일은 저장소에 커밋해 Rust 없이도 C#이 빌드되게 한다.
fn main() {
    println!("cargo:rerun-if-changed=src/ffi.rs");
    csbindgen::Builder::default()
        .input_extern_file("src/ffi.rs")
        .csharp_dll_name("foldy_copy")
        .csharp_namespace("Foldy.Copy.Native")
        .csharp_class_name("NativeMethods")
        .csharp_class_accessibility("internal")
        .generate_csharp_file("../../src/Foldy.Copy.Native/NativeMethods.g.cs")
        .expect("csbindgen failed");
}
