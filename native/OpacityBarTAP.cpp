// SPDX-License-Identifier: MIT
// Copyright (c) 2026 moller.studio
// OpacityBar: https://github.com/mollerstudio/OpacityBar

// =====================================================================================
//  OpacbarTAP.cpp - the small helper that Opacbar loads into explorer.exe  (protocol v6)
// =====================================================================================
//
//  PLAIN-ENGLISH SUMMARY (read this first if you are auditing Opacbar)
//  -------------------------------------------------------------------
//  Windows 11 draws its taskbar with XAML. A normal app cannot reach into Explorer's
//  XAML, so Opacbar uses Microsoft's official, documented XAML Diagnostics API
//  (InitializeXamlDiagnosticsEx - the same API Visual Studio's Live Visual Tree and
//  TranslucentTB use) to load this DLL into explorer.exe. This file is the complete
//  source of that DLL. Its only job is GENERIC STYLING OF TASKBAR XAML ELEMENTS:
//  changing properties such as a background brush, margin, corner radius or opacity of
//  elements that live inside the taskbar / system tray XAML trees, and putting the
//  original values back afterwards.
//
//  WHAT IT DOES
//    1. Explorer's XAML runtime creates our COM object and hands it the "visual tree
//       service" (IVisualTreeService) and the diagnostics object (IXamlDiagnostics).
//    2. We subscribe to visual-tree change notifications. For every XAML element we
//       remember: its handle (a number), its parent's handle, its child index, the UI
//       thread that owns it, and its XAML type name and x:Name (needed to match style
//       rules such as "Taskbar.TaskbarBackground > *#BackgroundFill"). Type names and
//       x:Names are interned in a small bounded table. No text content is ever read.
//    3. On every XAML UI thread that reports tree changes we create one invisible,
//       message-only window (class "OpacbarTAP.Sink"), plus one on our own small control
//       thread (used for diagnostics and UNLOAD). Rules only ever touch elements inside
//       taskbar trees (trees containing Taskbar.TaskbarFrame / SystemTray.SystemTrayFrame).
//    4. Opacbar.exe sends those windows (see native/PROTOCOL.md):
//         RULES (WM_COPYDATA 'OPAC') - a list of "selector, property, value" rules.
//                  We set those properties on the matching taskbar elements and restore
//                  the original values of properties no longer targeted.
//         RESTORE - put every original value back.
//         DUMP    - send back a text outline of the taskbar element tree (types, x:Names,
//                   sizes, margins, paddings, corner radii, background brushes) so the
//                   user can write rules. Only to the window handle given by the caller.
//         PING / VERSION / STATS - "are you alive?" / "which protocol?" / element counts
//         UNLOAD  - restore everything, unsubscribe, destroy the windows and go inert
//                   (so a newer helper can be loaded without restarting Explorer).
//         SET     - the old v1 "paint the background rectangle" message (still works).
//    5. Values are created by the XAML runtime itself: either from a short string
//       (IVisualTreeService::CreateInstance, e.g. "#00000000" or "4,0,4,0") or from a
//       XAML snippet (Windows.UI.Xaml.Markup.XamlReader.Load, e.g. an AcrylicBrush).
//       The special value "@blur:<radius>:<#AARRGGBB>" creates our own XAML brush
//       (XamlCompositionBrushBase) that draws a Gaussian blur of what is behind the
//       taskbar plus a tint, using the public Windows.UI.Composition effect API. It is
//       purely visual: it never reads pixels back or sends them anywhere.
//
//  WHAT IT DOES NOT DO
//    - No network access of any kind (no sockets, no HTTP, no WinINet/WinHTTP).
//    - No file or registry reads or writes. No settings stored here at all.
//    - No keyboard or mouse hooks, no keylogging, no screen capture, no clipboard access.
//      (The blur brush is rendered by the system compositor on the GPU; this DLL never
//      sees its pixels.)
//    - Does not read or send text content shown by Explorer (window titles, labels...).
//      The DUMP only contains element type names, x:Names and layout/brush values.
//    - Only modifies elements inside the taskbar / system tray trees.
//    - Does not start processes or elevate privileges. Its only imports are system DLLs
//      that Explorer has already loaded (kernel32, user32, ole32/oleaut32, and the
//      WinRT core in combase via api-ms-win-core-winrt-* API sets); WinRT objects used
//      are in-box Windows.UI.Xaml / Windows.UI.Composition / Windows.Foundation classes.
//    - Talks only to whoever sends the window messages above; the worst any other
//      program could do through them is restyle the taskbar or read its element outline.
//
//  SAFETY RULES THIS FILE FOLLOWS (a crash here would crash Explorer)
//    - Every HRESULT is checked; failures are skipped, never fatal.
//    - C++ exceptions are only used by the C++/WinRT blur brush code; every place where
//      one could occur is wrapped in try/catch and turned into an HRESULT, and every
//      COM method / window procedure / XAML override of ours has a catch-all, so no
//      exception can ever cross into Explorer. Our own allocations use nothrow / HeapAlloc.
//    - All shared state is guarded by one critical section, and the lock is NEVER held
//      while calling into XAML, creating windows or sending messages.
//    - Properties are only changed on the UI thread that owns the element (each sink
//      window applies the rules to its own thread's elements).
//    - Inside the tree-change callback we only update bookkeeping and post a message;
//      the actual property changes happen later from our own window procedure.
//    - Sizes are bounded: rules document <= 256 KB / 512 rules, <= 4096 applied
//      properties, element map <= 262k slots, dump <= 20000 lines.
//    - The DLL holds a reference on itself once it has subscribed, so Explorer never
//      unloads code that still has live callbacks, threads or window procedures. Only a
//      completed UNLOAD (unsubscribed, no callback in flight, all windows destroyed, no
//      object of ours still held by Explorer) drops that reference.
//    - The XAML Diagnostics interfaces and the XamlReader ABI below are hand-declared (no
//      SDK xamlom.h needed). IIDs, struct layouts and vtable order were checked against
//      xamlom.idl (Windows SDK / mingw-w64) and Windows.UI.Xaml.winmd. Do not reorder them.
//      The blur brush uses the SDK's C++/WinRT headers (include\<ver>\cppwinrt).
//    - Blur brushes are created and released only on the UI thread of the element they
//      are set on (rule application runs there). The DLL is not unloaded while any of our
//      C++/WinRT objects is still alive (winrt::get_module_lock()).
//
//  Build (MSVC, from a VS developer prompt; the Windows SDK provides C++/WinRT):
//    cl /nologo /LD /O2 /std:c++17 /EHsc /permissive- /GS /W4 /DUNICODE /D_UNICODE ^
//       native\OpacbarTAP.cpp /Fe:OpacbarTAP.x64.dll /link /DEF:native\OpacbarTAP.def ^
//       kernel32.lib ole32.lib oleaut32.lib user32.lib dxguid.lib windowsapp.lib
//
//  License: MIT (see LICENSE in the repository root).
// =====================================================================================

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <unknwn.h>      // before C++/WinRT: enables classic COM interfaces in winrt::implements
#include <ole2.h>
#include <oleauto.h>
#include <ocidl.h>
#include <wchar.h>
#include <stdlib.h>
#include <new>
#include <d2d1_1.h>       // D2D1 effect property enums (no D2D function is called)
#include <d2d1effects.h>  // CLSID_D2D1GaussianBlur / Flood / Composite (dxguid.lib)

// C++/WinRT - used only for the @blur brush (see "Adjustable blur brush" below).
#ifdef GetCurrentTime
#undef GetCurrentTime     // clashes with Windows.UI.Xaml.Media.Animation
#endif
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Effects.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Hosting.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Shapes.h>

// -------------------------------------------------------------------------------------
//  IPC contract with Opacbar.exe (see native/PROTOCOL.md, keep in sync with src/*.cs)
// -------------------------------------------------------------------------------------
#define OPAC_SINK_CLASS       L"OpacbarTAP.Sink"
#define WM_OPAC_SET           (WM_APP + 0x10)  // v1: wParam = 0xAARRGGBB, lParam = flags.
#define WM_OPAC_RESTORE       (WM_APP + 0x11)  // Restores everything. Returns #properties restored.
#define WM_OPAC_PING          (WM_APP + 0x12)  // Returns OPAC_PING_REPLY.
#define WM_OPAC_PENDING       (WM_APP + 0x13)  // Internal: (re)apply the current rules on this thread.
#define WM_OPAC_DUMP          (WM_APP + 0x14)  // wParam = HWND to send the 'OTRE' dump to.
#define WM_OPAC_VERSION       (WM_APP + 0x15)  // Returns OPAC_PROTOCOL_VERSION.
#define WM_OPAC_STATS         (WM_APP + 0x16)  // Returns packed element counts + state bits.
#define WM_OPAC_UNLOAD        (WM_APP + 0x17)  // Restore everything, unsubscribe, go inert. Returns 1 if done.
#define WM_OPAC_SHUTDOWN      (WM_APP + 0x18)  // Internal: restore this thread, destroy this sink.
#define WM_OPAC_UNLOAD_RUN    (WM_APP + 0x19)  // Internal: control thread finishes the unload.
#define OPAC_PING_REPLY       0x0BAC
#define OPAC_PROTOCOL_VERSION 6   // 2 = first v2 build (no STATS/UNLOAD), 3 = + STATS/UNLOAD, 4 = tree service via IXamlDiagnostics,
                                  // 5 = originals kept alive (exact restore of Explorer's acrylic brush),
                                  // 6 = "@blur:<radius>:<#AARRGGBB>" adjustable blur brush values
                                  // Bump on EVERY helper change: Opacbar restarts Explorer to load a newer helper.
#define OPAC_FLAG_ON          0x1              // v1 SET bit0: override on
#define OPAC_FLAG_HIDE_STROKE 0x2              // v1 SET bit1: make BackgroundStroke transparent too
#define OPAC_COPYDATA_RULES   0x4F504143       // 'OPAC'
#define OPAC_COPYDATA_TREE    0x4F545245       // 'OTRE'
#define OPAC_MAX_DOC_BYTES    (256 * 1024)
#define OPAC_MAX_RULES        512
#define OPAC_MAX_SEGS         16
#define OPAC_MAX_DUMP_LINES   20000
#define OPAC_MAX_DUMP_CHARS   ((SIZE_T)8 * 1024 * 1024)

// CLSID of our TAP object: {6C3B1F0A-3D52-4E8B-9A41-0B5C2E7D9F11}
static const GUID kCLSID_OpacbarTap =
    { 0x6c3b1f0a, 0x3d52, 0x4e8b, { 0x9a, 0x41, 0x0b, 0x5c, 0x2e, 0x7d, 0x9f, 0x11 } };

// Standard COM IIDs, defined locally so we don't need uuid.lib.
static const GUID kIID_IUnknown =
    { 0x00000000, 0x0000, 0x0000, { 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
static const GUID kIID_IClassFactory =
    { 0x00000001, 0x0000, 0x0000, { 0xc0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 } };
static const GUID kIID_IObjectWithSite =   // {FC4801A3-2BA9-11CF-A229-00AA003D7352}
    { 0xfc4801a3, 0x2ba9, 0x11cf, { 0xa2, 0x29, 0x00, 0xaa, 0x00, 0x3d, 0x73, 0x52 } };

// -------------------------------------------------------------------------------------
//  Minimal hand-declared WinRT ABI (no WinRT headers needed)
// -------------------------------------------------------------------------------------
namespace winrtabi {

struct HSTRING_OPAQUE__;                 // HSTRING is an opaque pointer
typedef HSTRING_OPAQUE__* OPAC_HSTR;

// IInspectable {AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90}: IUnknown + 3 methods.
struct IInspectableAbi : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetIids(ULONG* iidCount, IID** iids) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetRuntimeClassName(OPAC_HSTR* className) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetTrustLevel(int* trustLevel) = 0;
};

// Windows.UI.Xaml.Markup.IXamlReaderStatics {9891C6BD-534F-4955-B85A-8A8DC0DCA602}
// (verified against C:\Windows\System32\WinMetadata\Windows.UI.Xaml.winmd:
//  methods Load(String) -> Object, LoadWithInitialTemplateValidation(String) -> Object)
static const GUID IID_IXamlReaderStatics =
    { 0x9891c6bd, 0x534f, 0x4955, { 0xb8, 0x5a, 0x8a, 0x8d, 0xc0, 0xdc, 0xa6, 0x02 } };
struct IXamlReaderStatics : public IInspectableAbi {
    virtual HRESULT STDMETHODCALLTYPE Load(OPAC_HSTR xaml, IInspectableAbi** result) = 0;
    virtual HRESULT STDMETHODCALLTYPE LoadWithInitialTemplateValidation(OPAC_HSTR xaml, IInspectableAbi** result) = 0;
};

typedef HRESULT (WINAPI* PFN_WindowsCreateString)(const WCHAR* sourceString, UINT32 length, OPAC_HSTR* string);
typedef HRESULT (WINAPI* PFN_WindowsDeleteString)(OPAC_HSTR string);
typedef HRESULT (WINAPI* PFN_RoGetActivationFactory)(OPAC_HSTR activatableClassId, REFIID iid, void** factory);

} // namespace winrtabi

using winrtabi::OPAC_HSTR;
using winrtabi::IInspectableAbi;
using winrtabi::IXamlReaderStatics;

// -------------------------------------------------------------------------------------
//  Hand-declared subset of xamlom.h (XAML Diagnostics). Verified against xamlom.idl.
//  Enums are 32-bit in MIDL, hence the explicit ": int".
// -------------------------------------------------------------------------------------
namespace xamlom {

typedef UINT64 InstanceHandle;   // "unsigned hyper"

enum VisualMutationType : int { Add = 0, Remove = 1 };

enum BaseValueSource : int {
    BaseValueSourceUnknown = 0, BaseValueSourceDefault = 1, BaseValueSourceBuiltInStyle = 2,
    BaseValueSourceStyle = 3, BaseValueSourceLocal = 4, Inherited = 5, DefaultStyleTrigger = 6,
    TemplateTrigger = 7, StyleTrigger = 8, ImplicitStyleReference = 9, ParentTemplate = 10,
    ParentTemplateTrigger = 11, Animation = 12, Coercion = 13, BaseValueSourceVisualState = 14
};

enum VisualElementState : int { ErrorResolved = 0, ErrorResourceNotFound = 1, ErrorInvalidResource = 2 };

// MetadataBit (only the bit we use)
static const INT64 IsValueHandle = 0x1;

struct SourceInfo {
    BSTR FileName;
    unsigned int LineNumber;
    unsigned int ColumnNumber;
    unsigned int CharPosition;
    BSTR Hash;
};

struct ParentChildRelation {
    InstanceHandle Parent;
    InstanceHandle Child;
    unsigned int ChildIndex;
};

struct VisualElement {
    InstanceHandle Handle;
    SourceInfo SrcInfo;
    BSTR Type;
    BSTR Name;
    unsigned int NumChildren;
};

struct PropertyChainSource {
    InstanceHandle Handle;
    BSTR TargetType;
    BSTR Name;
    BaseValueSource Source;
    SourceInfo SrcInfo;
};

struct PropertyChainValue {
    unsigned int Index;
    BSTR Type;
    BSTR DeclaringType;
    BSTR ValueType;
    BSTR ItemType;
    BSTR Value;
    BOOL Overridden;
    INT64 MetadataBits;   // "hyper"
    BSTR PropertyName;
    unsigned int PropertyChainIndex;
};

struct EnumType {
    BSTR Name;
    SAFEARRAY* ValueInts;
    SAFEARRAY* ValueStrings;
};

struct CollectionElementValue {
    unsigned int Index;
    BSTR ValueType;
    BSTR Value;
    INT64 MetadataBits;
};

#if defined(_WIN64)
static_assert(sizeof(SourceInfo) == 32, "SourceInfo layout");
static_assert(sizeof(ParentChildRelation) == 24, "ParentChildRelation layout");
static_assert(sizeof(VisualElement) == 64, "VisualElement layout");
static_assert(sizeof(PropertyChainSource) == 64, "PropertyChainSource layout");
static_assert(sizeof(PropertyChainValue) == 80, "PropertyChainValue layout");
static_assert(sizeof(CollectionElementValue) == 32, "CollectionElementValue layout");
#endif

// uuid(aa7a8931-80e4-4fec-8f3b-553f87b4966e)
static const GUID IID_IVisualTreeServiceCallback =
    { 0xaa7a8931, 0x80e4, 0x4fec, { 0x8f, 0x3b, 0x55, 0x3f, 0x87, 0xb4, 0x96, 0x6e } };
struct IVisualTreeServiceCallback : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) = 0;
};

// uuid(bad9eb88-ae77-4397-b948-5fa2db0a19ea)
static const GUID IID_IVisualTreeServiceCallback2 =
    { 0xbad9eb88, 0xae77, 0x4397, { 0xb9, 0x48, 0x5f, 0xa2, 0xdb, 0x0a, 0x19, 0xea } };
struct IVisualTreeServiceCallback2 : public IVisualTreeServiceCallback {
    virtual HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle element, VisualElementState elementState, LPCWSTR context) = 0;
};

// uuid(a593b11a-d17f-48bb-8f66-83910731c8a5) - all 12 methods declared, in vtable order.
static const GUID IID_IVisualTreeService =
    { 0xa593b11a, 0xd17f, 0x48bb, { 0x8f, 0x66, 0x83, 0x91, 0x07, 0x31, 0xc8, 0xa5 } };
struct IVisualTreeService : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE AdviseVisualTreeChange(IVisualTreeServiceCallback* callback) = 0;
    virtual HRESULT STDMETHODCALLTYPE UnadviseVisualTreeChange(IVisualTreeServiceCallback* callback) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetEnums(unsigned int* count, EnumType** enums) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateInstance(BSTR typeName, BSTR value, InstanceHandle* instanceHandle) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyValuesChain(
        InstanceHandle instanceHandle,
        unsigned int* sourceCount, PropertyChainSource** propertySources,
        unsigned int* propertyCount, PropertyChainValue** propertyValues) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetProperty(InstanceHandle instanceHandle, InstanceHandle value, unsigned int propertyIndex) = 0;
    virtual HRESULT STDMETHODCALLTYPE ClearProperty(InstanceHandle instanceHandle, unsigned int propertyIndex) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCollectionCount(InstanceHandle instanceHandle, unsigned int* collectionSize) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCollectionElements(
        InstanceHandle instanceHandle, unsigned int startIndex,
        unsigned int* elementCount, CollectionElementValue** elementValues) = 0;
    virtual HRESULT STDMETHODCALLTYPE AddChild(InstanceHandle parent, InstanceHandle child, unsigned int index) = 0;
    virtual HRESULT STDMETHODCALLTYPE RemoveChild(InstanceHandle parent, unsigned int index) = 0;
    virtual HRESULT STDMETHODCALLTYPE ClearChildren(InstanceHandle parent) = 0;
};

// uuid(18c9e2b6-3f43-4116-9f2b-ff935d7770d2) - all 8 methods declared, in vtable order.
static const GUID IID_IXamlDiagnostics =
    { 0x18c9e2b6, 0x3f43, 0x4116, { 0x9f, 0x2b, 0xff, 0x93, 0x5d, 0x77, 0x70, 0xd2 } };
struct IXamlDiagnostics : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetDispatcher(IInspectableAbi** dispatcher) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetUiLayer(IInspectableAbi** layer) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetApplication(IInspectableAbi** application) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle instanceHandle, IInspectableAbi** instance) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetHandleFromIInspectable(IInspectableAbi* instance, InstanceHandle* handle) = 0;
    virtual HRESULT STDMETHODCALLTYPE HitTest(RECT rect, unsigned int* count, InstanceHandle** instanceHandles) = 0;
    virtual HRESULT STDMETHODCALLTYPE RegisterInstance(IInspectableAbi* instance, InstanceHandle* instanceHandle) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetInitializationData(BSTR* initializationData) = 0;
};

} // namespace xamlom

using namespace xamlom;

// -------------------------------------------------------------------------------------
//  Global state (all guarded by g_lock)
// -------------------------------------------------------------------------------------
static HMODULE             g_module   = NULL;
static CRITICAL_SECTION    g_lock;
static volatile LONG       g_objects  = 0;    // live TapObject instances
static volatile LONG       g_locks    = 0;    // IClassFactory::LockServer count
static volatile LONG       g_advised  = 0;    // 1 once AdviseVisualTreeChange is started/succeeded
static volatile LONG       g_classOk  = 0;    // sink window class registered
static IVisualTreeService* g_service  = NULL; // kept for the life of the process once obtained
static IXamlDiagnostics*   g_diag     = NULL; // idem (may stay NULL: XAML values then fail)
static IVisualTreeServiceCallback2* g_callback = NULL;  // our advised callback (one ref) once advised
static volatile LONG       g_adviseState = 0; // 0 = not started, 1 = in progress, 2 = ok, 3 = failed
static volatile LONG       g_unloading   = 0; // 1 once UNLOAD started: everything goes inert
static volatile LONG       g_unloadRun   = 0; // control thread: run the unload cleanup after its loop
static volatile LONG       g_inCallback  = 0; // tree-change callbacks currently executing
static volatile LONG       g_selfRef     = 0; // we hold one module reference (instead of pinning)
static DWORD               g_tlsIndex    = TLS_OUT_OF_INDEXES;  // per thread: "sink checked"
static HWND                g_controlSink = NULL; // sink on our own control thread (diagnostics/unload)

struct Locker {
    Locker()  { EnterCriticalSection(&g_lock); }
    ~Locker() { LeaveCriticalSection(&g_lock); }
};

static const UINT64 kFnvOffset = 14695981039346656037ULL;
static const UINT64 kFnvPrime  = 1099511628211ULL;

static UINT64 HashChars(UINT64 h, const WCHAR* s, SIZE_T len)
{
    for (SIZE_T i = 0; i < len; i++) {
        h ^= (UINT64)(UINT16)s[i];
        h *= kFnvPrime;
    }
    return h;
}

static bool StrEq(const WCHAR* a, const WCHAR* b)
{
    return a && b && wcscmp(a, b) == 0;
}

static UINT64 ParseDecimal(const WCHAR* s)
{
    if (!s || !*s) return 0;
    UINT64 v = 0;
    for (; *s; s++) {
        if (*s < L'0' || *s > L'9') return 0;
        UINT64 d = (UINT64)(*s - L'0');
        if (v > (0xFFFFFFFFFFFFFFFFULL - d) / 10) return 0;
        v = v * 10 + d;
    }
    return v;
}

static bool IsSpace(WCHAR c) { return c == L' ' || c == L'\t' || c == L'\r' || c == L'\n'; }

static WCHAR* Trim(WCHAR* s)
{
    while (*s && IsSpace(*s)) s++;
    SIZE_T n = wcslen(s);
    while (n > 0 && IsSpace(s[n - 1])) s[--n] = 0;
    return s;
}

// Takes one extra reference on this DLL (released only by a completed UNLOAD), so Explorer
// never unloads code that still has live callbacks, threads or window procedures.
static bool TakeModuleRef()
{
    HMODULE h = NULL;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                              (LPCWSTR)(void*)&TakeModuleRef, &h) != FALSE;
}

// -------------------------------------------------------------------------------------
//  String interning (XAML type names, x:Names, property names). Bounded, never freed.
// -------------------------------------------------------------------------------------
#define ID_NONE          ((UINT16)0)        // empty string / no name
#define ID_FAIL          ((UINT16)0xFFFF)   // could not intern (never matches a rule)
#define POOL_MAX_STRINGS 16000u
#define POOL_HASH_SIZE   32768u             // power of two, > 2 * POOL_MAX_STRINGS
#define POOL_MAX_LEN     512u
#define POOL_MAX_CHARS   ((SIZE_T)2 * 1024 * 1024)

static WCHAR** g_poolStr   = NULL;   // [POOL_MAX_STRINGS], index = id
static UINT16* g_poolHash  = NULL;   // [POOL_HASH_SIZE], 0 = empty
static UINT32  g_poolCount = 1;      // id 0 reserved
static SIZE_T  g_poolChars = 0;

static UINT16 InternLocked(const WCHAR* s, SIZE_T len)
{
    if (!s || len == 0) return ID_NONE;
    if (len > POOL_MAX_LEN) return ID_FAIL;
    HANDLE heap = GetProcessHeap();
    if (!g_poolStr) {
        WCHAR** strs = (WCHAR**)HeapAlloc(heap, HEAP_ZERO_MEMORY, POOL_MAX_STRINGS * sizeof(WCHAR*));
        UINT16* hash = (UINT16*)HeapAlloc(heap, HEAP_ZERO_MEMORY, POOL_HASH_SIZE * sizeof(UINT16));
        if (!strs || !hash) {
            if (strs) HeapFree(heap, 0, strs);
            if (hash) HeapFree(heap, 0, hash);
            return ID_FAIL;
        }
        g_poolStr = strs;
        g_poolHash = hash;
    }
    const SIZE_T mask = POOL_HASH_SIZE - 1;
    SIZE_T i = (SIZE_T)HashChars(kFnvOffset, s, len) & mask;
    for (SIZE_T n = 0; n < POOL_HASH_SIZE; n++) {
        UINT16 id = g_poolHash[i];
        if (id == 0) break;
        const WCHAR* p = g_poolStr[id];
        if (wcsncmp(p, s, len) == 0 && p[len] == 0) return id;
        i = (i + 1) & mask;
    }
    if (g_poolHash[i] != 0) return ID_FAIL;   // table full (cannot happen: load < 50%)
    if (g_poolCount >= POOL_MAX_STRINGS || g_poolChars + len + 1 > POOL_MAX_CHARS) return ID_FAIL;
    WCHAR* copy = (WCHAR*)HeapAlloc(heap, 0, (len + 1) * sizeof(WCHAR));
    if (!copy) return ID_FAIL;
    CopyMemory(copy, s, len * sizeof(WCHAR));
    copy[len] = 0;
    UINT16 id = (UINT16)g_poolCount++;
    g_poolStr[id] = copy;
    g_poolHash[i] = id;
    g_poolChars += len + 1;
    return id;
}

static UINT16 Intern(const WCHAR* s, SIZE_T len)
{
    Locker l;
    return InternLocked(s, len);
}

// Strings are immutable once interned and the table never moves, so this is safe to
// call without the lock for an id that was obtained under the lock.
static const WCHAR* PoolStr(UINT16 id)
{
    if (id == ID_NONE) return L"";
    if (id == ID_FAIL || !g_poolStr || id >= POOL_MAX_STRINGS || !g_poolStr[id]) return L"?";
    return g_poolStr[id];
}

// -------------------------------------------------------------------------------------
//  Element map: handle -> { parent, thread, child index, type, name, flags }
//  (open addressing, no STL, no exceptions)
// -------------------------------------------------------------------------------------
#define SLOT_ROOT   0x1   // topmost known ancestor of a taskbar / tray frame
#define SLOT_DOOMED 0x2   // temporary mark while erasing a subtree

struct MapSlot {
    UINT64 key;
    UINT64 parent;
    DWORD  tid;          // UI thread that owns the element
    UINT32 childIndex;   // index among the parent's children when it was added
    UINT32 nChildren;    // known children (used to skip subtree scans on removal)
    UINT16 typeId;
    UINT16 nameId;
    UINT16 flags;
    UINT16 used;         // 0 = empty, 1 = live, 2 = deleted (tombstone)
};
static MapSlot* g_map      = NULL;
static SIZE_T   g_mapCap   = 0;    // power of two
static SIZE_T   g_mapLive  = 0;
static SIZE_T   g_mapUsed  = 0;    // live + tombstones
static const SIZE_T kMapMaxCap = (SIZE_T)1 << 18;   // 262k slots (~10 MB) hard ceiling
static const int    kMaxDepth  = 512;

static SIZE_T HashKey(UINT64 k)
{
    k ^= k >> 33; k *= 0xff51afd7ed558ccdULL; k ^= k >> 33;
    k *= 0xc4ceb9fe1a85ec53ULL; k ^= k >> 33;
    return (SIZE_T)k;
}

static bool MapRehash(SIZE_T newCap)
{
    MapSlot* fresh = (MapSlot*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, newCap * sizeof(MapSlot));
    if (!fresh) return false;
    SIZE_T mask = newCap - 1;
    for (SIZE_T i = 0; i < g_mapCap; i++) {
        if (g_map[i].used != 1) continue;
        SIZE_T j = HashKey(g_map[i].key) & mask;
        while (fresh[j].used) j = (j + 1) & mask;
        fresh[j] = g_map[i];
    }
    if (g_map) HeapFree(GetProcessHeap(), 0, g_map);
    g_map = fresh;
    g_mapCap = newCap;
    g_mapUsed = g_mapLive;
    return true;
}

static MapSlot* MapFind(UINT64 key)
{
    if (!g_map || key == 0) return NULL;
    SIZE_T mask = g_mapCap - 1;
    SIZE_T i = HashKey(key) & mask;
    for (SIZE_T n = 0; n < g_mapCap; n++) {
        MapSlot* s = &g_map[i];
        if (s->used == 0) return NULL;
        if (s->used == 1 && s->key == key) return s;
        i = (i + 1) & mask;
    }
    return NULL;
}

static void MapPut(UINT64 key, UINT64 parent, DWORD tid, UINT32 childIndex, UINT16 typeId, UINT16 nameId)
{
    if (key == 0) return;
    MapSlot* existing = MapFind(key);
    if (existing) {
        UINT64 oldParent = existing->parent;
        existing->parent = parent;
        existing->tid = tid;
        existing->childIndex = childIndex;
        existing->typeId = typeId;
        existing->nameId = nameId;
        if (oldParent != parent) {
            MapSlot* op = MapFind(oldParent);
            if (op && op->nChildren) op->nChildren--;
            MapSlot* np = MapFind(parent);
            if (np) np->nChildren++;
        }
        return;
    }

    if (!g_map || (g_mapUsed + 1) * 2 > g_mapCap) {
        SIZE_T want = 1024;
        while (want < (g_mapLive + 1) * 4 && want < kMapMaxCap) want <<= 1;
        if (want < g_mapCap) want = g_mapCap;
        // Grow, or compact when tombstones pile up; at the size cap just keep probing.
        if (!g_map || want > g_mapCap || (g_mapUsed - g_mapLive) > g_mapCap / 4) {
            if (!MapRehash(want) && !g_map) return;
        }
    }
    SIZE_T mask = g_mapCap - 1;
    SIZE_T i = HashKey(key) & mask;
    for (SIZE_T n = 0; n < g_mapCap; n++) {
        MapSlot* s = &g_map[i];
        if (s->used != 1) {
            if (s->used == 0) g_mapUsed++;
            s->key = key; s->parent = parent; s->tid = tid; s->childIndex = childIndex;
            s->nChildren = 0; s->typeId = typeId; s->nameId = nameId; s->flags = 0; s->used = 1;
            g_mapLive++;
            MapSlot* p = MapFind(parent);
            if (p) p->nChildren++;
            return;
        }
        i = (i + 1) & mask;
    }
    // Table completely full (only possible at kMapMaxCap): silently drop.
}

static void MapEraseSlot(MapSlot* s)
{
    if (!s || s->used != 1) return;
    MapSlot* p = MapFind(s->parent);
    if (p && p != s && p->nChildren) p->nChildren--;
    s->used = 2;
    s->key = 0;
    s->flags = 0;
    g_mapLive--;
}

// Erases `root` and every known element below it.
static void MapEraseSubtree(UINT64 root)
{
    MapSlot* r = MapFind(root);
    if (!r) return;
    if (r->nChildren) {
        // Phase 1: mark (chains must stay intact while we walk them).
        for (SIZE_T i = 0; i < g_mapCap; i++) {
            MapSlot* s = &g_map[i];
            if (s->used != 1 || s->key == root) continue;
            UINT64 cur = s->parent;
            for (int d = 0; d < kMaxDepth && cur != 0; d++) {
                if (cur == root) { s->flags |= SLOT_DOOMED; break; }
                MapSlot* p = MapFind(cur);
                if (!p) break;
                cur = p->parent;
            }
        }
        // Phase 2: erase.
        for (SIZE_T i = 0; i < g_mapCap; i++) {
            MapSlot* s = &g_map[i];
            if (s->used == 1 && (s->flags & SLOT_DOOMED)) MapEraseSlot(s);
        }
        r = MapFind(root);
    }
    MapEraseSlot(r);
}

// Marks the topmost known ancestor-or-self of `key` as a taskbar tree root.
static void MarkRootLocked(UINT64 key)
{
    MapSlot* cur = MapFind(key);
    if (!cur) return;
    for (int d = 0; d < kMaxDepth && cur->parent != 0; d++) {
        MapSlot* p = MapFind(cur->parent);
        if (!p) break;
        cur = p;
    }
    cur->flags |= SLOT_ROOT;
}

// True if the element or one of its known ancestors is a marked taskbar tree root.
static bool InTaskbarTreeLocked(const MapSlot* s)
{
    for (int d = 0; d < kMaxDepth && s; d++) {
        if (s->flags & SLOT_ROOT) return true;
        if (s->parent == 0) return false;
        s = MapFind(s->parent);
    }
    return false;
}

static bool HasMarkedAncestorLocked(const MapSlot* s)
{
    UINT64 cur = s->parent;
    for (int d = 0; d < kMaxDepth && cur != 0; d++) {
        const MapSlot* p = MapFind(cur);
        if (!p) return false;
        if (p->flags & SLOT_ROOT) return true;
        cur = p->parent;
    }
    return false;
}

// True if `element` is `ancestor` or lies below it (according to the map).
static bool IsUnderLocked(UINT64 element, UINT64 ancestor)
{
    UINT64 cur = element;
    for (int d = 0; d < kMaxDepth && cur != 0; d++) {
        if (cur == ancestor) return true;
        const MapSlot* s = MapFind(cur);
        if (!s) return false;
        cur = s->parent;
    }
    return false;
}

static bool IsFrameType(const WCHAR* type)
{
    if (!type) return false;
    return StrEq(type, L"Taskbar.TaskbarFrame") ||
           StrEq(type, L"SystemTray.SystemTrayFrame") ||
           wcsstr(type, L"TaskbarBackground") != NULL;   // fallback marker (v1 heuristic)
}

// -------------------------------------------------------------------------------------
//  Rules
// -------------------------------------------------------------------------------------
static const BYTE kValClear = 0;   // "@clear"
static const BYTE kValPlain = 1;   // CreateInstance(type, string)
static const BYTE kValXaml  = 2;   // XamlReader.Load
static const BYTE kValBlur  = 3;   // "@blur:<radius>:<#AARRGGBB>[:host]" -> our blur brush
static const UINT16 kMatchDone = 0xFFFF;

// Value kinds whose object we create ourselves and keep alive in Record::xamlObj.
static bool IsObjectKind(BYTE kind) { return kind == kValXaml || kind == kValBlur; }

// Parsed "@blur:<radius>:<#AARRGGBB>[:host]".
struct BlurSpec {
    float radius;        // Gaussian blur standard deviation in DIPs, clamped to 0..100
    BYTE  a, r, g, b;    // tint drawn over the blurred backdrop (straight alpha)
    bool  host;          // ":host" = sample the host backdrop (behind the window) instead
};

static int HexDigit(WCHAR c)
{
    if (c >= L'0' && c <= L'9') return c - L'0';
    if (c >= L'a' && c <= L'f') return c - L'a' + 10;
    if (c >= L'A' && c <= L'F') return c - L'A' + 10;
    return -1;
}

// Accepts "@blur:R", "@blur:R:#AARRGGBB", "@blur:R:#RRGGBB" and any of those + ":host".
// R is a decimal number (e.g. 30 or 12.5). No locale-dependent parsing.
static bool ParseBlurSpec(const WCHAR* v, BlurSpec* out)
{
    ZeroMemory(out, sizeof(*out));
    if (!v || wcsncmp(v, L"@blur:", 6) != 0) return false;
    const WCHAR* p = v + 6;
    UINT32 whole = 0;
    int digits = 0;
    while (*p >= L'0' && *p <= L'9') {
        whole = whole * 10 + (UINT32)(*p - L'0');
        if (whole > 100000) return false;
        digits++;
        p++;
    }
    float frac = 0.0f, scale = 0.1f;
    if (*p == L'.') {
        p++;
        while (*p >= L'0' && *p <= L'9') {
            if (scale > 1e-6f) frac += (float)(*p - L'0') * scale;
            scale *= 0.1f;
            digits++;
            p++;
        }
    }
    if (digits == 0) return false;
    float radius = (float)whole + frac;
    if (radius > 100.0f) radius = 100.0f;
    if (radius < 0.0f) radius = 0.0f;
    out->radius = radius;

    if (*p == L':' && p[1] == L'#') {
        p += 2;
        int n = 0;
        while (n < 9 && HexDigit(p[n]) >= 0) n++;
        if (n != 8 && n != 6) return false;
        UINT32 argb = 0;
        for (int i = 0; i < n; i++) argb = (argb << 4) | (UINT32)HexDigit(p[i]);
        if (n == 6) argb |= 0xFF000000u;
        out->a = (BYTE)(argb >> 24);
        out->r = (BYTE)(argb >> 16);
        out->g = (BYTE)(argb >> 8);
        out->b = (BYTE)argb;
        p += n;
    }
    if (*p == L':') {
        if (wcscmp(p + 1, L"host") != 0) return false;
        out->host = true;
        return true;
    }
    return *p == 0;
}

struct Seg {
    UINT16 typeId;
    UINT16 nameId;
    INT32  index;     // -1 = any
    BYTE   anyType;
    BYTE   hasName;
    BYTE   never;     // could not intern: never matches
    BYTE   pad;
};

struct Rule {
    Seg          segs[OPAC_MAX_SEGS];
    UINT32       nSegs;
    UINT16       propId;
    BYTE         kind;
    const WCHAR* value;      // points into RuleSet::text
    UINT32       valueLen;
    UINT64       valueHash;
};

struct RuleSet {
    LONG   refs;     // guarded by g_lock
    UINT32 gen;
    UINT32 count;
    WCHAR* text;
    Rule*  rules;
};

static RuleSet* g_rules = NULL;
static UINT32   g_gen   = 0;    // last rule-set generation (never 0 once assigned)
static UINT32   g_pass  = 0;    // apply pass counter

static void FreeRuleSet(RuleSet* rs)
{
    if (!rs) return;
    HANDLE heap = GetProcessHeap();
    if (rs->text) HeapFree(heap, 0, rs->text);
    if (rs->rules) HeapFree(heap, 0, rs->rules);
    HeapFree(heap, 0, rs);
}

static void ReleaseRules(RuleSet* rs)
{
    if (!rs) return;
    bool last;
    {
        Locker l;
        last = (--rs->refs == 0);
    }
    if (last) FreeRuleSet(rs);
}

static bool ParseSeg(WCHAR* s, Seg* g)
{
    g->index = -1;
    SIZE_T len = wcslen(s);
    if (len > 0 && s[len - 1] == L']') {
        WCHAR* lb = wcsrchr(s, L'[');
        if (!lb) return false;
        s[len - 1] = 0;
        WCHAR* num = Trim(lb + 1);
        if (!*num) return false;
        UINT32 v = 0;
        for (WCHAR* p = num; *p; p++) {
            if (*p < L'0' || *p > L'9') return false;
            v = v * 10 + (UINT32)(*p - L'0');
            if (v > 1000000) return false;
        }
        g->index = (INT32)v;
        *lb = 0;
    }
    WCHAR* name = NULL;
    WCHAR* hash = wcschr(s, L'#');
    if (hash) { *hash = 0; name = hash + 1; }
    WCHAR* type = Trim(s);
    if (*type == 0 || (type[0] == L'*' && type[1] == 0)) {
        if (*type == 0 && !name && g->index < 0) return false;   // empty segment
        g->anyType = 1;
    } else {
        UINT16 id = Intern(type, wcslen(type));
        if (id == ID_FAIL || id == ID_NONE) g->never = 1;
        else g->typeId = id;
    }
    if (name) {
        name = Trim(name);
        g->hasName = 1;
        if (*name) {
            UINT16 id = Intern(name, wcslen(name));
            if (id == ID_FAIL) g->never = 1;
            else g->nameId = id;
        } else {
            g->nameId = ID_NONE;   // "Type#" = element without a name
        }
    }
    return true;
}

static bool ParseSelector(WCHAR* sel, Rule* r)
{
    UINT32 n = 0;
    WCHAR* cur = sel;
    for (;;) {
        WCHAR* gt = wcschr(cur, L'>');
        if (gt) *gt = 0;
        WCHAR* seg = Trim(cur);
        if (!*seg || n >= OPAC_MAX_SEGS) return false;
        if (!ParseSeg(seg, &r->segs[n])) return false;
        n++;
        if (!gt) break;
        cur = gt + 1;
    }
    r->nSegs = n;
    return n > 0;
}

static UINT64 HashValue(BYTE kind, const WCHAR* value, SIZE_T len)
{
    UINT64 h = kFnvOffset;
    h ^= (UINT64)kind;
    h *= kFnvPrime;
    h = HashChars(h, value, len);
    return h ? h : 1;
}

static bool ParseRuleLine(WCHAR* line, Rule* r)
{
    if (!*line) return false;
    WCHAR* t1 = wcschr(line, L'\t');
    if (!t1) return false;
    *t1 = 0;
    WCHAR* prop = t1 + 1;
    WCHAR* t2 = wcschr(prop, L'\t');
    if (!t2) return false;
    *t2 = 0;
    WCHAR* value = Trim(t2 + 1);
    prop = Trim(prop);
    if (!*prop || !*value) return false;
    SIZE_T plen = wcslen(prop);
    if (plen > 128) return false;
    if (!ParseSelector(line, r)) return false;
    UINT16 pid = Intern(prop, plen);
    if (pid == ID_NONE || pid == ID_FAIL) return false;
    r->propId = pid;
    r->value = value;
    r->valueLen = (UINT32)wcslen(value);
    BlurSpec blur;
    if (wcscmp(value, L"@clear") == 0) r->kind = kValClear;
    else if (wcsncmp(value, L"@blur:", 6) == 0) {
        if (!ParseBlurSpec(value, &blur)) return false;   // malformed: ignore the line
        r->kind = kValBlur;
    }
    else if (value[0] == L'<')         r->kind = kValXaml;
    else                               r->kind = kValPlain;
    r->valueHash = HashValue(r->kind, value, r->valueLen);
    return true;
}

static WCHAR* NextLine(WCHAR* p, WCHAR** next)
{
    WCHAR* nl = wcschr(p, L'\n');
    if (nl) { *nl = 0; *next = nl + 1; }
    else *next = NULL;
    SIZE_T len = wcslen(p);
    if (len > 0 && p[len - 1] == L'\r') p[len - 1] = 0;
    return p;
}

// Returns NULL on a bad header (*headerOk = false) or out of memory (*headerOk = true).
static RuleSet* ParseRules(const WCHAR* data, SIZE_T nChars, bool* headerOk)
{
    *headerOk = false;
    HANDLE heap = GetProcessHeap();
    RuleSet* rs = (RuleSet*)HeapAlloc(heap, HEAP_ZERO_MEMORY, sizeof(RuleSet));
    WCHAR* text = (WCHAR*)HeapAlloc(heap, 0, (nChars + 1) * sizeof(WCHAR));
    Rule* rules = (Rule*)HeapAlloc(heap, HEAP_ZERO_MEMORY, OPAC_MAX_RULES * sizeof(Rule));
    if (!rs || !text || !rules) {
        if (rs) HeapFree(heap, 0, rs);
        if (text) HeapFree(heap, 0, text);
        if (rules) HeapFree(heap, 0, rules);
        // Can't tell whether the header was fine; report it as such so callers return 0.
        *headerOk = true;
        return NULL;
    }
    if (nChars) CopyMemory(text, data, nChars * sizeof(WCHAR));
    text[nChars] = 0;
    rs->text = text;
    rs->rules = rules;

    WCHAR* p = text;
    if (*p == 0xFEFF) p++;
    WCHAR* next = NULL;
    WCHAR* header = Trim(NextLine(p, &next));
    if (wcscmp(header, L"opacbar-rules 2") != 0) {
        FreeRuleSet(rs);
        return NULL;
    }
    *headerOk = true;
    while (next && rs->count < OPAC_MAX_RULES) {
        WCHAR* line = NextLine(next, &next);
        if (ParseRuleLine(line, &rules[rs->count])) rs->count++;
        else ZeroMemory(&rules[rs->count], sizeof(Rule));
    }
    return rs;
}

static bool SegMatch(const Seg& g, const MapSlot* s)
{
    if (g.never) return false;
    if (!g.anyType && s->typeId != g.typeId) return false;
    if (g.hasName && s->nameId != g.nameId) return false;
    if (g.index >= 0 && s->childIndex != (UINT32)g.index) return false;
    return true;
}

// Selector semantics (PROTOCOL.md): last segment = target, middle segments = direct
// parents in order, first segment = nearest ancestor (any distance) satisfying it.
static bool MatchRuleLocked(const MapSlot* e, const Rule& r)
{
    const UINT32 k = r.nSegs;
    if (k == 0 || !SegMatch(r.segs[k - 1], e)) return false;
    const MapSlot* cur = e;
    for (int i = (int)k - 2; i >= 1; i--) {
        if (cur->parent == 0) return false;
        cur = MapFind(cur->parent);
        if (!cur || !SegMatch(r.segs[i], cur)) return false;
    }
    if (k >= 2) {
        UINT64 p = cur->parent;
        for (int d = 0; d < kMaxDepth && p != 0; d++) {
            const MapSlot* a = MapFind(p);
            if (!a) return false;
            if (SegMatch(r.segs[0], a)) return true;
            p = a->parent;
        }
        return false;
    }
    return true;
}

// -------------------------------------------------------------------------------------
//  Applied records: one per (element, property) we changed, with the exact original.
// -------------------------------------------------------------------------------------
#define MAX_RECORDS 4096
static const BYTE kOrigNone   = 0;   // restore with ClearProperty
static const BYTE kOrigHandle = 1;   // original local value was an object handle
static const BYTE kOrigString = 2;   // original local value was a plain value (type + string)

struct Record {
    UINT64           element;      // 0 = free slot
    DWORD            tid;
    UINT16           propId;
    BYTE             origKind;
    BYTE             valueKind;
    UINT32           propIndex;
    UINT32           gen;          // rule-set generation last applied (0 = reserved, not applied)
    UINT32           pass;         // last apply pass that kept this record
    UINT64           valueHash;
    UINT64           valueHandle;  // handle we set (0 for @clear)
    UINT64           origHandle;
    BSTR             origType;
    BSTR             origValue;
    IInspectableAbi* xamlObj;      // one reference to a XamlReader-created value we set
    IInspectableAbi* origObj;      // one reference to the ORIGINAL object value (kOrigHandle), so it
                                   // cannot be destroyed while replaced and can be put back exactly
                                   // (e.g. Explorer's acrylic taskbar brush).
};
static Record g_recs[MAX_RECORDS];
static UINT32 g_recHigh = 0;   // records live in [0, g_recHigh)

static Record* FindRecordLocked(UINT64 element, UINT16 propId)
{
    for (UINT32 i = 0; i < g_recHigh; i++)
        if (g_recs[i].element == element && g_recs[i].propId == propId) return &g_recs[i];
    return NULL;
}

static Record* AllocRecordLocked()
{
    for (UINT32 i = 0; i < MAX_RECORDS; i++) {
        if (g_recs[i].element == 0) {
            ZeroMemory(&g_recs[i], sizeof(Record));
            if (i + 1 > g_recHigh) g_recHigh = i + 1;
            return &g_recs[i];
        }
    }
    return NULL;
}

// Frees what a record owns. Must be called WITHOUT g_lock (may release a XAML object).
static void FreeRecordResources(Record& r, bool releaseObject)
{
    SysFreeString(r.origType);
    SysFreeString(r.origValue);
    r.origType = NULL;
    r.origValue = NULL;
    if (r.xamlObj && releaseObject) r.xamlObj->Release();
    r.xamlObj = NULL;
    if (r.origObj && releaseObject) r.origObj->Release();
    r.origObj = NULL;
}

// Thread went away (or its id got reused): forget its records without touching XAML.
// XAML objects are deliberately leaked: their UI thread is gone, releasing them is unsafe.
static void DropThreadRecordsLocked(DWORD tid)
{
    for (UINT32 i = 0; i < g_recHigh; i++) {
        Record& r = g_recs[i];
        if (r.element == 0 || r.tid != tid) continue;
        SysFreeString(r.origType);
        SysFreeString(r.origValue);
        ZeroMemory(&r, sizeof(Record));
    }
}

// -------------------------------------------------------------------------------------
//  Per UI thread: sink window + small cache of plain values created with CreateInstance
// -------------------------------------------------------------------------------------
#define MAX_THREADS 64
#define VALUE_CACHE 32

struct CachedValue {
    UINT64 hash;      // 0 = empty
    UINT64 handle;
};
struct ThreadState {
    DWORD       tid;            // 0 = free slot
    HWND        sink;
    BOOL        pendingPosted;  // a WM_OPAC_PENDING is already queued
    UINT32      nextEvict;
    CachedValue cache[VALUE_CACHE];
};
static ThreadState g_threads[MAX_THREADS];

static ThreadState* FindThread(DWORD tid)
{
    for (int i = 0; i < MAX_THREADS; i++)
        if (g_threads[i].tid == tid) return &g_threads[i];
    return NULL;
}

static ThreadState* FindOrAddThread(DWORD tid)
{
    ThreadState* ts = FindThread(tid);
    if (ts) return ts;
    for (int i = 0; i < MAX_THREADS; i++) {
        if (g_threads[i].tid == 0) {
            ZeroMemory(&g_threads[i], sizeof(ThreadState));
            g_threads[i].tid = tid;
            return &g_threads[i];
        }
    }
    // Full: reclaim a slot whose thread died (its window is gone without WM_NCDESTROY).
    for (int i = 0; i < MAX_THREADS; i++) {
        if (g_threads[i].sink && !IsWindow(g_threads[i].sink)) {
            DropThreadRecordsLocked(g_threads[i].tid);
            ZeroMemory(&g_threads[i], sizeof(ThreadState));
            g_threads[i].tid = tid;
            return &g_threads[i];
        }
    }
    return NULL;
}

// -------------------------------------------------------------------------------------
//  WinRT entry points (looked up in the already loaded combase.dll)
// -------------------------------------------------------------------------------------
struct WinRtFns {
    winrtabi::PFN_WindowsCreateString   create;
    winrtabi::PFN_WindowsDeleteString   del;
    winrtabi::PFN_RoGetActivationFactory getFactory;
};
static WinRtFns g_winrt;
static int      g_winrtState = 0;   // 0 = not resolved yet, 1 = ok, 2 = unavailable

static bool ResolveWinRt(WinRtFns* out)
{
    Locker l;
    if (g_winrtState == 0) {
        HMODULE m = GetModuleHandleW(L"combase.dll");
        if (m) {
            g_winrt.create = reinterpret_cast<winrtabi::PFN_WindowsCreateString>(
                GetProcAddress(m, "WindowsCreateString"));
            g_winrt.del = reinterpret_cast<winrtabi::PFN_WindowsDeleteString>(
                GetProcAddress(m, "WindowsDeleteString"));
            g_winrt.getFactory = reinterpret_cast<winrtabi::PFN_RoGetActivationFactory>(
                GetProcAddress(m, "RoGetActivationFactory"));
        }
        g_winrtState = (g_winrt.create && g_winrt.del && g_winrt.getFactory) ? 1 : 2;
    }
    if (g_winrtState != 1) return false;
    *out = g_winrt;
    return true;
}

// -------------------------------------------------------------------------------------
//  XAML property helpers (call WITHOUT holding g_lock, on the element's UI thread)
// -------------------------------------------------------------------------------------
struct Chain {
    unsigned int         srcCount;
    PropertyChainSource* srcs;
    unsigned int         propCount;
    PropertyChainValue*  vals;
};

static void ChainFree(Chain* c)
{
    if (c->srcs) {
        for (unsigned int i = 0; i < c->srcCount; i++) {
            SysFreeString(c->srcs[i].TargetType);
            SysFreeString(c->srcs[i].Name);
            SysFreeString(c->srcs[i].SrcInfo.FileName);
            SysFreeString(c->srcs[i].SrcInfo.Hash);
        }
        CoTaskMemFree(c->srcs);
    }
    if (c->vals) {
        for (unsigned int i = 0; i < c->propCount; i++) {
            SysFreeString(c->vals[i].Type);
            SysFreeString(c->vals[i].DeclaringType);
            SysFreeString(c->vals[i].ValueType);
            SysFreeString(c->vals[i].ItemType);
            SysFreeString(c->vals[i].Value);
            SysFreeString(c->vals[i].PropertyName);
        }
        CoTaskMemFree(c->vals);
    }
    ZeroMemory(c, sizeof(Chain));
}

static bool ChainGet(IVisualTreeService* svc, UINT64 element, Chain* c)
{
    ZeroMemory(c, sizeof(Chain));
    HRESULT hr = svc->GetPropertyValuesChain(element, &c->srcCount, &c->srcs, &c->propCount, &c->vals);
    if (FAILED(hr)) {
        // [out] data is undefined on failure: never free it (a tiny leak beats a double free).
        ZeroMemory(c, sizeof(Chain));
        return false;
    }
    if (!c->srcs) c->srcCount = 0;
    if (!c->vals) c->propCount = 0;
    return true;
}

// first = first entry with that property name (its Index is the property index),
// eff = first non-overridden entry (the effective value).
static void ChainFind(const Chain* c, const WCHAR* name, int* first, int* eff)
{
    *first = -1;
    *eff = -1;
    if (!c || !c->vals) return;
    for (unsigned int i = 0; i < c->propCount; i++) {
        if (!StrEq(c->vals[i].PropertyName, name)) continue;
        if (*first < 0) *first = (int)i;
        if (!c->vals[i].Overridden && *eff < 0) *eff = (int)i;
    }
}

static const WCHAR* ChainValue(const Chain* c, const WCHAR* name)
{
    int first, eff;
    ChainFind(c, name, &first, &eff);
    int i = eff >= 0 ? eff : first;
    if (i < 0) return NULL;
    return c->vals[i].Value;
}

static bool EndsWith(const WCHAR* s, const WCHAR* suffix, SIZE_T* prefixLen)
{
    if (!s) return false;
    SIZE_T a = wcslen(s), b = wcslen(suffix);
    if (a < b || wcscmp(s + (a - b), suffix) != 0) return false;
    *prefixLen = a - b;
    return true;
}

// "X.Media.Brush" (abstract) -> "X.Media.SolidColorBrush".
static bool MapBrushType(const WCHAR* type, WCHAR* out, SIZE_T outCount)
{
    SIZE_T pre = 0;
    if (!EndsWith(type, L".Media.Brush", &pre)) return false;
    static const WCHAR kSuffix[] = L".Media.SolidColorBrush";
    SIZE_T sl = ARRAYSIZE(kSuffix) - 1;
    if (pre + sl + 1 > outCount) return false;
    CopyMemory(out, type, pre * sizeof(WCHAR));
    CopyMemory(out + pre, kSuffix, (sl + 1) * sizeof(WCHAR));
    return true;
}

struct Ctx {
    IVisualTreeService* svc;
    IXamlDiagnostics*   diag;
    DWORD               tid;
    UINT32              gen;
    UINT32              pass;
};

// Returns a handle for (type, value) on the current UI thread, cached per thread.
static UINT64 GetPlainValue(const Ctx& cx, const WCHAR* type, const WCHAR* value, UINT32 valueLen,
                            bool forceNew, bool* fromCache)
{
    *fromCache = false;
    UINT64 key = HashChars(kFnvOffset, type, wcslen(type));
    key ^= 0x1F;
    key *= kFnvPrime;
    key = HashChars(key, value, valueLen);
    if (key == 0) key = 1;

    if (!forceNew) {
        Locker l;
        ThreadState* ts = FindThread(cx.tid);
        if (ts) {
            for (int i = 0; i < VALUE_CACHE; i++) {
                if (ts->cache[i].hash == key && ts->cache[i].handle) {
                    *fromCache = true;
                    return ts->cache[i].handle;
                }
            }
        }
    }

    BSTR bt = SysAllocString(type);
    BSTR bv = SysAllocStringLen(value, valueLen);
    UINT64 handle = 0;
    if (bt && bv) {
        if (FAILED(cx.svc->CreateInstance(bt, bv, &handle))) handle = 0;
    }
    SysFreeString(bt);
    SysFreeString(bv);
    if (!handle) return 0;

    Locker l;
    ThreadState* ts = FindThread(cx.tid);
    if (ts) {
        int slot = -1;
        for (int i = 0; i < VALUE_CACHE && slot < 0; i++)
            if (ts->cache[i].hash == key) slot = i;
        for (int i = 0; i < VALUE_CACHE && slot < 0; i++)
            if (ts->cache[i].hash == 0) slot = i;
        if (slot < 0) slot = (int)(ts->nextEvict++ % VALUE_CACHE);
        ts->cache[slot].hash = key;
        ts->cache[slot].handle = handle;
    }
    return handle;
}

static HRESULT SetPlain(const Ctx& cx, UINT64 e, UINT32 idx, const Rule& r, const Chain* c,
                        int first, int eff, UINT64* outHandle)
{
    *outHandle = 0;
    if (!c || first < 0) return E_FAIL;

    const WCHAR* cands[3];
    int n = 0;
    WCHAR mapped[300];
    const WCHAR* t = c->vals[first].Type;
    if (MapBrushType(t, mapped, ARRAYSIZE(mapped))) cands[n++] = mapped;
    else if (t && *t) cands[n++] = t;
    const WCHAR* vt = c->vals[eff >= 0 ? eff : first].ValueType;
    if (vt && *vt && !(n > 0 && StrEq(cands[0], vt))) cands[n++] = vt;
    static const WCHAR kSolid[] = L"Windows.UI.Xaml.Media.SolidColorBrush";
    if (r.value[0] == L'#') {
        bool dup = false;
        for (int i = 0; i < n; i++) if (StrEq(cands[i], kSolid)) dup = true;
        if (!dup) cands[n++] = kSolid;
    }

    HRESULT hr = E_FAIL;
    for (int i = 0; i < n; i++) {
        bool cached = false;
        UINT64 h = GetPlainValue(cx, cands[i], r.value, r.valueLen, false, &cached);
        if (!h) continue;
        hr = cx.svc->SetProperty(e, h, idx);
        if (FAILED(hr) && cached) {
            // Cached value may have gone stale: retry once with a fresh one.
            h = GetPlainValue(cx, cands[i], r.value, r.valueLen, true, &cached);
            if (!h) continue;
            hr = cx.svc->SetProperty(e, h, idx);
        }
        if (SUCCEEDED(hr)) {
            *outHandle = h;
            return hr;
        }
    }
    return FAILED(hr) ? hr : E_FAIL;
}

// Copies `text` adding the default XAML namespaces to the root element if missing.
static WCHAR* BuildXaml(const WCHAR* text, UINT32 len, UINT32* outLen)
{
    static const WCHAR kNs[]  = L" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    static const WCHAR kNsX[] = L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    const SIZE_T nsLen = ARRAYSIZE(kNs) - 1, nsxLen = ARRAYSIZE(kNsX) - 1;

    bool addNs = false, addNsX = false;
    UINT32 nameEnd = 0;
    if (len > 1 && text[0] == L'<' && text[1] != L'?' && text[1] != L'!') {
        UINT32 tagEnd = 0;
        while (tagEnd < len && text[tagEnd] != L'>') tagEnd++;
        if (tagEnd < len) {
            nameEnd = 1;
            while (nameEnd < tagEnd && !IsSpace(text[nameEnd]) && text[nameEnd] != L'/') nameEnd++;
            addNs = addNsX = true;
            for (UINT32 i = nameEnd; i < tagEnd; i++) {
                if (i + 6 <= tagEnd && wcsncmp(text + i, L"xmlns=", 6) == 0) addNs = false;
                if (i + 8 <= tagEnd && wcsncmp(text + i, L"xmlns:x=", 8) == 0) addNsX = false;
            }
        }
    }
    SIZE_T total = len + (addNs ? nsLen : 0) + (addNsX ? nsxLen : 0);
    WCHAR* buf = (WCHAR*)HeapAlloc(GetProcessHeap(), 0, (total + 1) * sizeof(WCHAR));
    if (!buf) return NULL;
    SIZE_T o = 0;
    if (addNs || addNsX) {
        CopyMemory(buf, text, nameEnd * sizeof(WCHAR)); o = nameEnd;
        if (addNs)  { CopyMemory(buf + o, kNs, nsLen * sizeof(WCHAR));   o += nsLen; }
        if (addNsX) { CopyMemory(buf + o, kNsX, nsxLen * sizeof(WCHAR)); o += nsxLen; }
        CopyMemory(buf + o, text + nameEnd, (len - nameEnd) * sizeof(WCHAR)); o += len - nameEnd;
    } else {
        CopyMemory(buf, text, len * sizeof(WCHAR)); o = len;
    }
    buf[o] = 0;
    *outLen = (UINT32)o;
    return buf;
}

// XamlReader.Load(text) -> object + diagnostics handle. On success the caller owns one
// reference to *objOut and keeps it while the value is applied (we do not rely on the
// diagnostics handle table to keep the object alive).
static HRESULT LoadXamlValue(IXamlDiagnostics* diag, const WCHAR* text, UINT32 len,
                             IInspectableAbi** objOut, UINT64* handleOut)
{
    *objOut = NULL;
    *handleOut = 0;
    if (!diag) return E_NOINTERFACE;
    WinRtFns fns;
    if (!ResolveWinRt(&fns)) return E_NOTIMPL;

    UINT32 xlen = 0;
    WCHAR* xaml = BuildXaml(text, len, &xlen);
    if (!xaml) return E_OUTOFMEMORY;

    static const WCHAR kClass[] = L"Windows.UI.Xaml.Markup.XamlReader";
    OPAC_HSTR hsClass = NULL, hsXaml = NULL;
    IXamlReaderStatics* reader = NULL;
    IInspectableAbi* obj = NULL;
    HRESULT hr = fns.create(kClass, (UINT32)(ARRAYSIZE(kClass) - 1), &hsClass);
    if (SUCCEEDED(hr)) hr = fns.create(xaml, xlen, &hsXaml);
    if (SUCCEEDED(hr)) hr = fns.getFactory(hsClass, winrtabi::IID_IXamlReaderStatics, (void**)&reader);
    if (SUCCEEDED(hr) && !reader) hr = E_NOINTERFACE;
    if (SUCCEEDED(hr)) hr = reader->Load(hsXaml, &obj);
    if (reader) reader->Release();
    if (hsXaml) fns.del(hsXaml);
    if (hsClass) fns.del(hsClass);
    HeapFree(GetProcessHeap(), 0, xaml);
    if (FAILED(hr)) {
        if (obj) obj->Release();
        return hr;
    }
    if (!obj) return E_FAIL;

    UINT64 h = 0;
    hr = diag->GetHandleFromIInspectable(obj, &h);
    if (FAILED(hr) || !h) {
        h = 0;
        hr = diag->RegisterInstance(obj, &h);
    }
    if (FAILED(hr) || !h) {
        obj->Release();
        return FAILED(hr) ? hr : E_FAIL;
    }
    *objOut = obj;
    *handleOut = h;
    return S_OK;
}

// -------------------------------------------------------------------------------------
//  Adjustable blur brush ("@blur:<radius>:<#AARRGGBB>[:host]")
//
//  Explorer's own taskbar brush blurs with a fixed strength, and an AcrylicBrush loaded
//  through XamlReader only shows its fallback color on the taskbar. So, like the
//  "WindhawkBlur" brush of Windhawk's "Windows 11 Taskbar Styler" mod (idea credited
//  there to TranslucentTB's XamlBlurBrush), we build our own XAML brush:
//    a class deriving from Windows.UI.Xaml.Media.XamlCompositionBrushBase whose
//    OnConnected() creates a Windows.UI.Composition effect brush
//        backdrop (CompositionBackdropBrush, or HostBackdropBrush with ":host")
//          -> D2D1 Gaussian blur (standard deviation = radius)
//          -> D2D1 Flood (tint) composited SOURCE_OVER on top
//    and assigns it to CompositionBrush. The effect graph is described to the
//    compositor with small objects implementing IGraphicsEffect + IGraphicsEffectSource
//    + IGraphicsEffectD2D1Interop (what Win2D's effect classes do).
//  This is an independent implementation of that technique (no code copied).
//  It only creates XAML / Composition objects on the element's own UI thread.
// -------------------------------------------------------------------------------------
namespace opacfx {

namespace wf  = winrt::Windows::Foundation;
namespace wge = winrt::Windows::Graphics::Effects;
namespace wuc = winrt::Windows::UI::Composition;
namespace wux = winrt::Windows::UI::Xaml;

// GRAPHICS_EFFECT_PROPERTY_MAPPING (windows.graphics.effects.interop.h), 32-bit enum.
enum PropMapping : int {
    kMapUnknown = 0, kMapDirect = 1, kMapVectorX = 2, kMapVectorY = 3, kMapVectorZ = 4,
    kMapVectorW = 5, kMapRectToVector4 = 6, kMapRadiansToDegrees = 7,
    kMapColorMatrixAlphaMode = 8, kMapColorToVector3 = 9, kMapColorToVector4 = 10
};

// ABI::Windows::Graphics::Effects::IGraphicsEffectD2D1Interop, hand-declared in vtable
// order from windows.graphics.effects.interop.h (the ABI pointer types are passed as void**:
// GetProperty returns an ABI IPropertyValue*, GetSource an ABI IGraphicsEffectSource*).
struct __declspec(uuid("2FC57384-A068-44D7-A331-30982FCF7177"))
IGraphicsEffectD2D1InteropAbi : public ::IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetEffectId(GUID* id) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetNamedPropertyMapping(LPCWSTR name, UINT* index, PropMapping* mapping) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyCount(UINT* count) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetProperty(UINT index, void** value) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetSource(UINT index, void** source) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetSourceCount(UINT* count) = 0;
};

static const UINT kPropFloat  = 0;
static const UINT kPropUInt   = 1;
static const UINT kPropFloat4 = 2;
static const UINT kMaxProps   = 3;
static const UINT kMaxSources = 2;

struct EffectProp {
    const wchar_t* name;
    UINT           kind;
    float          f[4];
    UINT32         u;
    PropMapping    mapping;
};

// One D2D1 effect node. Property i of the array is D2D property index i.
struct Effect : winrt::implements<Effect, wge::IGraphicsEffect, wge::IGraphicsEffectSource,
                                  IGraphicsEffectD2D1InteropAbi> {
    GUID          m_id = {};
    winrt::hstring m_name;
    EffectProp    m_props[kMaxProps] = {};
    UINT          m_nProps = 0;
    wge::IGraphicsEffectSource m_src[kMaxSources] = { nullptr, nullptr };
    UINT          m_nSrc = 0;

    void SetFloat(UINT index, const wchar_t* name, float v)
    {
        if (index >= kMaxProps) return;
        EffectProp& p = m_props[index];
        p.name = name; p.kind = kPropFloat; p.f[0] = v; p.mapping = kMapDirect;
        if (index + 1 > m_nProps) m_nProps = index + 1;
    }
    void SetUInt(UINT index, const wchar_t* name, UINT32 v)
    {
        if (index >= kMaxProps) return;
        EffectProp& p = m_props[index];
        p.name = name; p.kind = kPropUInt; p.u = v; p.mapping = kMapDirect;
        if (index + 1 > m_nProps) m_nProps = index + 1;
    }
    void SetColor(UINT index, const wchar_t* name, BYTE a, BYTE r, BYTE g, BYTE b)
    {
        if (index >= kMaxProps) return;
        EffectProp& p = m_props[index];
        p.name = name; p.kind = kPropFloat4;
        p.f[0] = r / 255.0f; p.f[1] = g / 255.0f; p.f[2] = b / 255.0f; p.f[3] = a / 255.0f;
        p.mapping = kMapColorToVector4;
        if (index + 1 > m_nProps) m_nProps = index + 1;
    }
    void SetSource(UINT index, wge::IGraphicsEffectSource const& s)
    {
        if (index >= kMaxSources) return;
        m_src[index] = s;
        if (index + 1 > m_nSrc) m_nSrc = index + 1;
    }

    // IGraphicsEffect
    winrt::hstring Name() { return m_name; }
    void Name(winrt::hstring const& name) { m_name = name; }

    // IGraphicsEffectD2D1Interop
    HRESULT STDMETHODCALLTYPE GetEffectId(GUID* id) noexcept override
    {
        if (!id) return E_POINTER;
        *id = m_id;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetNamedPropertyMapping(LPCWSTR name, UINT* index, PropMapping* mapping) noexcept override
    {
        if (!name || !index || !mapping) return E_POINTER;
        for (UINT i = 0; i < m_nProps; i++) {
            if (m_props[i].name && _wcsicmp(m_props[i].name, name) == 0) {
                *index = i;
                *mapping = m_props[i].mapping;
                return S_OK;
            }
        }
        return E_INVALIDARG;
    }
    HRESULT STDMETHODCALLTYPE GetPropertyCount(UINT* count) noexcept override
    {
        if (!count) return E_POINTER;
        *count = m_nProps;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetProperty(UINT index, void** value) noexcept override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (index >= m_nProps) return E_BOUNDS;
        try {
            const EffectProp& p = m_props[index];
            wf::IInspectable boxed{ nullptr };
            if (p.kind == kPropUInt)
                boxed = wf::PropertyValue::CreateUInt32(p.u);
            else if (p.kind == kPropFloat4)
                boxed = wf::PropertyValue::CreateSingleArray(winrt::array_view<float const>(&p.f[0], &p.f[0] + 4));
            else
                boxed = wf::PropertyValue::CreateSingle(p.f[0]);
            wf::IPropertyValue pv = boxed.as<wf::IPropertyValue>();
            *value = winrt::detach_abi(pv);
            return S_OK;
        } catch (...) {
            return winrt::to_hresult();
        }
    }
    HRESULT STDMETHODCALLTYPE GetSource(UINT index, void** source) noexcept override
    {
        if (!source) return E_POINTER;
        *source = nullptr;
        if (index >= m_nSrc || !m_src[index]) return E_BOUNDS;
        wge::IGraphicsEffectSource s = m_src[index];
        *source = winrt::detach_abi(s);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetSourceCount(UINT* count) noexcept override
    {
        if (!count) return E_POINTER;
        *count = m_nSrc;
        return S_OK;
    }
};

// The XAML brush. Holds only plain values + the compositor; the composition brush is
// created when XAML connects the brush to a rendered element and closed on disconnect.
struct BlurBrush : wux::Media::XamlCompositionBrushBaseT<BlurBrush> {
    BlurBrush(wuc::Compositor const& compositor, BlurSpec const& spec)
        : m_compositor(compositor), m_spec(spec) {}

    void OnConnected()
    {
        try {
            if (this->CompositionBrush()) return;
            this->CompositionBrush(Build());
        } catch (...) {
            // Leave the brush empty: XAML then draws FallbackColor (= the tint).
        }
    }

    void OnDisconnected()
    {
        try {
            wuc::CompositionBrush brush = this->CompositionBrush();
            if (brush) {
                this->CompositionBrush(nullptr);
                brush.Close();
            }
        } catch (...) {
        }
    }

private:
    wuc::CompositionBrush Build()
    {
        wuc::CompositionBrush backdrop = m_spec.host
            ? wuc::CompositionBrush(m_compositor.CreateHostBackdropBrush())
            : wuc::CompositionBrush(m_compositor.CreateBackdropBrush());

        const bool blur = m_spec.radius > 0.0f;
        const bool tint = m_spec.a != 0;
        if (!blur && !tint) return backdrop;   // radius 0, no tint: plain (clear) backdrop

        wge::IGraphicsEffectSource stack = wuc::CompositionEffectSourceParameter(L"backdrop");
        wge::IGraphicsEffect top{ nullptr };

        if (blur) {
            winrt::com_ptr<Effect> e = winrt::make_self<Effect>();
            e->m_id = CLSID_D2D1GaussianBlur;
            e->m_name = L"OpacbarBlur";
            e->SetFloat(D2D1_GAUSSIANBLUR_PROP_STANDARD_DEVIATION, L"BlurAmount", m_spec.radius);
            e->SetUInt(D2D1_GAUSSIANBLUR_PROP_OPTIMIZATION, L"Optimization",
                       (UINT32)D2D1_GAUSSIANBLUR_OPTIMIZATION_BALANCED);
            // HARD: edges stay opaque instead of fading to transparent at the bar's border.
            e->SetUInt(D2D1_GAUSSIANBLUR_PROP_BORDER_MODE, L"BorderMode", (UINT32)D2D1_BORDER_MODE_HARD);
            e->SetSource(0, stack);
            top = e.as<wge::IGraphicsEffect>();
            stack = e.as<wge::IGraphicsEffectSource>();
        }
        if (tint) {
            winrt::com_ptr<Effect> flood = winrt::make_self<Effect>();
            flood->m_id = CLSID_D2D1Flood;
            flood->m_name = L"OpacbarTint";
            flood->SetColor(D2D1_FLOOD_PROP_COLOR, L"Color", m_spec.a, m_spec.r, m_spec.g, m_spec.b);

            winrt::com_ptr<Effect> comp = winrt::make_self<Effect>();
            comp->m_id = CLSID_D2D1Composite;
            comp->m_name = L"OpacbarComposite";
            comp->SetUInt(D2D1_COMPOSITE_PROP_MODE, L"Mode", (UINT32)D2D1_COMPOSITE_MODE_SOURCE_OVER);
            comp->SetSource(0, stack);                                        // destination
            comp->SetSource(1, flood.as<wge::IGraphicsEffectSource>());       // source (on top)
            top = comp.as<wge::IGraphicsEffect>();
        }

        wuc::CompositionEffectFactory factory = m_compositor.CreateEffectFactory(top);
        wuc::CompositionEffectBrush brush = factory.CreateBrush();
        brush.SetSourceParameter(L"backdrop", backdrop);
        return brush;
    }

    wuc::Compositor m_compositor{ nullptr };
    BlurSpec        m_spec;
};

// Creates a blur brush for `rawElement` (an IInspectable* of the target element) on the
// current (UI) thread. On success *out holds one reference to the brush.
static HRESULT CreateBlurBrush(IInspectableAbi* rawElement, BlurSpec const& spec, IInspectableAbi** out)
{
    *out = NULL;
    try {
        wf::IInspectable element{ nullptr };
        winrt::copy_from_abi(element, static_cast<void*>(rawElement));
        wux::UIElement ui = element.try_as<wux::UIElement>();
        if (!ui) return E_NOINTERFACE;
        wuc::Compositor compositor = wux::Hosting::ElementCompositionPreview::GetElementVisual(ui).Compositor();
        if (!compositor) return E_FAIL;

        wf::IInspectable brush = winrt::make<BlurBrush>(compositor, spec);
        winrt::Windows::UI::Color tint{};
        tint.A = spec.a; tint.R = spec.r; tint.G = spec.g; tint.B = spec.b;
        // Shown by XAML when effects are unavailable (e.g. transparency effects off).
        brush.as<wux::Media::XamlCompositionBrushBase>().FallbackColor(tint);

        void* abi = nullptr;
        winrt::copy_to_abi(brush, abi);
        *out = static_cast<IInspectableAbi*>(abi);
        return *out ? S_OK : E_FAIL;
    } catch (...) {
        if (*out) { (*out)->Release(); *out = NULL; }
        return winrt::to_hresult();
    }
}

// Fallback when the diagnostics handle route cannot set the brush: Shape.Fill directly.
static HRESULT SetFillDirect(IInspectableAbi* rawElement, IInspectableAbi* rawBrush)
{
    try {
        wf::IInspectable element{ nullptr }, brushObj{ nullptr };
        winrt::copy_from_abi(element, static_cast<void*>(rawElement));
        winrt::copy_from_abi(brushObj, static_cast<void*>(rawBrush));
        wux::Shapes::Shape shape = element.try_as<wux::Shapes::Shape>();
        wux::Media::Brush brush = brushObj.try_as<wux::Media::Brush>();
        if (!shape || !brush) return E_NOINTERFACE;
        shape.Fill(brush);
        return S_OK;
    } catch (...) {
        return winrt::to_hresult();
    }
}

// Number of our C++/WinRT objects (brushes, effect nodes) still alive anywhere.
static UINT32 LiveObjects()
{
    return static_cast<UINT32>(winrt::get_module_lock());
}

} // namespace opacfx

// "@blur:..." -> new brush set on (e, idx). On success *objOut holds one reference and
// *handleOut is its diagnostics handle (0 if it was set through the Shape.Fill fallback).
static HRESULT ApplyBlurValue(const Ctx& cx, UINT64 e, UINT32 idx, const WCHAR* propName, const Rule& r,
                              IInspectableAbi** objOut, UINT64* handleOut)
{
    *objOut = NULL;
    *handleOut = 0;
    if (!cx.diag) return E_NOINTERFACE;
    BlurSpec spec;
    if (!ParseBlurSpec(r.value, &spec)) return E_INVALIDARG;

    IInspectableAbi* el = NULL;
    HRESULT hr = cx.diag->GetIInspectableFromHandle(e, &el);
    if (FAILED(hr) || !el) return FAILED(hr) ? hr : E_FAIL;

    IInspectableAbi* brush = NULL;
    hr = opacfx::CreateBlurBrush(el, spec, &brush);
    if (SUCCEEDED(hr)) {
        UINT64 h = 0;
        HRESULT hh = cx.diag->GetHandleFromIInspectable(brush, &h);
        if (FAILED(hh) || !h) {
            h = 0;
            hh = cx.diag->RegisterInstance(brush, &h);
        }
        if (SUCCEEDED(hh) && h) hr = cx.svc->SetProperty(e, h, idx);
        else hr = FAILED(hh) ? hh : E_FAIL;
        if (FAILED(hr) && StrEq(propName, L"Fill")) {
            h = 0;
            hr = opacfx::SetFillDirect(el, brush);
        }
        if (SUCCEEDED(hr)) {
            *objOut = brush;
            *handleOut = h;
            brush = NULL;
        }
    }
    if (brush) brush->Release();
    el->Release();
    return hr;
}

// Applies one rule to one (element, property). Returns true if it is now applied & tracked.
static bool ApplyOne(const Ctx& cx, UINT64 e, UINT16 propId, const Rule& r, const Chain* c)
{
    const WCHAR* propName = PoolStr(propId);
    int first = -1, eff = -1;
    if (c) ChainFind(c, propName, &first, &eff);

    bool hasRec = false;
    UINT32 idx = 0;
    BYTE recKind = 0;
    UINT64 recHash = 0, recHandle = 0;
    {
        Locker l;
        Record* rec = FindRecordLocked(e, propId);
        if (rec) {
            hasRec = true;
            idx = rec->propIndex;
            recKind = rec->valueKind;
            recHash = rec->valueHash;
            recHandle = rec->valueHandle;
        }
    }

    if (!hasRec) {
        if (first < 0) return false;   // element has no such property (or chain unavailable)
        idx = c->vals[first].Index;

        // Capture the exact original local value (if any) before we change anything.
        BYTE origKind = kOrigNone;
        UINT64 origHandle = 0;
        BSTR origType = NULL, origValue = NULL;
        IInspectableAbi* origObj = NULL;
        if (eff >= 0) {
            const PropertyChainValue& v = c->vals[eff];
            bool isLocal = c->srcs && v.PropertyChainIndex < c->srcCount &&
                           c->srcs[v.PropertyChainIndex].Source == BaseValueSourceLocal;
            if (isLocal) {
                if (v.MetadataBits & IsValueHandle) {
                    origHandle = ParseDecimal(v.Value);
                    if (origHandle) {
                        origKind = kOrigHandle;
                        // Hold the original object alive while our value replaces it.
                        IXamlDiagnostics* d = NULL;
                        {
                            Locker l;
                            d = g_diag;
                            if (d) d->AddRef();
                        }
                        if (d) {
                            if (FAILED(d->GetIInspectableFromHandle(origHandle, &origObj))) origObj = NULL;
                            d->Release();
                        }
                    }
                } else if (v.Value) {
                    const WCHAR* t = (v.ValueType && *v.ValueType) ? v.ValueType : v.Type;
                    if (t && *t) {
                        origType = SysAllocString(t);
                        origValue = SysAllocStringLen(v.Value, SysStringLen(v.Value));
                        if (origType && origValue) origKind = kOrigString;
                        else {
                            SysFreeString(origType); origType = NULL;
                            SysFreeString(origValue); origValue = NULL;
                        }
                    }
                }
            }
        }

        bool reserved = false;
        {
            Locker l;
            Record* rec = FindRecordLocked(e, propId);   // cannot exist, but be safe
            if (!rec) {
                rec = AllocRecordLocked();
                if (rec) {
                    rec->element = e;
                    rec->tid = cx.tid;
                    rec->propId = propId;
                    rec->propIndex = idx;
                    rec->origKind = origKind;
                    rec->origHandle = origHandle;
                    rec->origType = origType;
                    rec->origValue = origValue;
                    rec->origObj = origObj;
                    rec->gen = 0;
                    reserved = true;
                }
            }
        }
        if (!reserved) {
            SysFreeString(origType);
            SysFreeString(origValue);
            if (origObj) origObj->Release();
            return false;
        }
    }

    HRESULT hr = E_FAIL;
    UINT64 valHandle = 0;
    IInspectableAbi* newObj = NULL;
    if (r.kind == kValClear) {
        hr = cx.svc->ClearProperty(e, idx);
    } else if (IsObjectKind(r.kind)) {
        if (hasRec && recKind == r.kind && recHash == r.valueHash && recHandle) {
            valHandle = recHandle;               // same value as before: reuse our object
            hr = cx.svc->SetProperty(e, valHandle, idx);
        }
        if (FAILED(hr)) {
            valHandle = 0;
            if (r.kind == kValBlur) {
                hr = ApplyBlurValue(cx, e, idx, propName, r, &newObj, &valHandle);   // sets it too
            } else {
                hr = LoadXamlValue(cx.diag, r.value, r.valueLen, &newObj, &valHandle);
                if (SUCCEEDED(hr)) hr = cx.svc->SetProperty(e, valHandle, idx);
            }
        }
    } else {
        hr = SetPlain(cx, e, idx, r, c, first, eff, &valHandle);
    }

    if (FAILED(hr)) {
        if (newObj) newObj->Release();
        if (!hasRec) {
            // Drop our reservation: the property was not changed.
            Record taken;
            ZeroMemory(&taken, sizeof(taken));
            bool got = false;
            {
                Locker l;
                Record* rec = FindRecordLocked(e, propId);
                if (rec && rec->gen == 0) { taken = *rec; ZeroMemory(rec, sizeof(Record)); got = true; }
            }
            if (got) FreeRecordResources(taken, true);
        }
        return false;
    }

    IInspectableAbi* oldObj = NULL;
    bool tracked = false;
    {
        Locker l;
        Record* rec = FindRecordLocked(e, propId);
        if (rec) {
            rec->gen = cx.gen;
            rec->pass = cx.pass;
            rec->valueHash = r.valueHash;
            rec->valueKind = r.kind;
            rec->valueHandle = valHandle;
            if (newObj) {
                oldObj = rec->xamlObj;
                rec->xamlObj = newObj;
                newObj = NULL;
            } else if (!IsObjectKind(r.kind)) {
                oldObj = rec->xamlObj;
                rec->xamlObj = NULL;
            }
            tracked = true;
        }
    }
    if (oldObj) oldObj->Release();   // no longer set on the element
    if (newObj) newObj->Release();   // record vanished meanwhile: the element holds its own ref
    return tracked;
}

static bool RestoreRecord(IVisualTreeService* svc, const Record& r)
{
    if (r.origKind == kOrigHandle && r.origHandle &&
        SUCCEEDED(svc->SetProperty(r.element, r.origHandle, r.propIndex)))
        return true;
    if (r.origKind == kOrigHandle && r.origObj) {
        // Handle no longer registered: re-register the (still alive) original object and retry.
        IXamlDiagnostics* d = NULL;
        {
            Locker l;
            d = g_diag;
            if (d) d->AddRef();
        }
        if (d) {
            InstanceHandle h = 0;
            bool ok = SUCCEEDED(d->GetHandleFromIInspectable(r.origObj, &h)) && h &&
                      SUCCEEDED(svc->SetProperty(r.element, h, r.propIndex));
            d->Release();
            if (ok) return true;
        }
    }
    if (r.origKind == kOrigString && r.origType && r.origValue) {
        UINT64 h = 0;
        if (SUCCEEDED(svc->CreateInstance(r.origType, r.origValue, &h)) && h &&
            SUCCEEDED(svc->SetProperty(r.element, h, r.propIndex)))
            return true;
    }
    return SUCCEEDED(svc->ClearProperty(r.element, r.propIndex));
}

// Restores every record of this thread that the current pass did not (re)apply.
static UINT RestoreStale(const Ctx& cx)
{
    UINT restored = 0;
    for (;;) {
        Record taken;
        ZeroMemory(&taken, sizeof(taken));
        bool got = false;
        {
            Locker l;
            for (UINT32 i = 0; i < g_recHigh; i++) {
                Record& r = g_recs[i];
                if (r.element && r.tid == cx.tid && r.pass != cx.pass) {
                    taken = r;
                    ZeroMemory(&r, sizeof(Record));
                    got = true;
                    break;
                }
            }
        }
        if (!got) break;
        if (RestoreRecord(cx.svc, taken)) restored++;
        FreeRecordResources(taken, true);
    }
    return restored;
}

struct Match {
    UINT64 element;
    UINT16 propId;
    UINT16 rule;    // index into the rule set, or kMatchDone
    UINT32 pad;
};
#define MAX_MATCHES MAX_RECORDS

// Applies the current rule set to every element owned by the calling UI thread and
// restores whatever is no longer targeted. Returns #(element, property) pairs applied.
static UINT ApplyRulesOnThisThread(UINT* restoredOut)
{
    if (restoredOut) *restoredOut = 0;
    Ctx cx;
    ZeroMemory(&cx, sizeof(cx));
    cx.tid = GetCurrentThreadId();
    RuleSet* rs = NULL;
    {
        Locker l;
        cx.svc = g_service;
        cx.diag = g_diag;
        rs = g_rules;
        if (rs) rs->refs++;
        cx.gen = rs ? rs->gen : 0;
        if (++g_pass == 0) ++g_pass;
        cx.pass = g_pass;
        ThreadState* ts = FindThread(cx.tid);
        if (ts) ts->pendingPosted = FALSE;
    }
    if (!cx.svc) { ReleaseRules(rs); return 0; }

    Match* m = NULL;
    UINT32 nm = 0;
    if (rs && rs->count) {
        m = (Match*)HeapAlloc(GetProcessHeap(), 0, MAX_MATCHES * sizeof(Match));
        if (!m) { ReleaseRules(rs); return 0; }   // keep everything as is
        Locker l;
        for (SIZE_T i = 0; i < g_mapCap; i++) {
            const MapSlot* s = &g_map[i];
            if (s->used != 1 || s->tid != cx.tid) continue;
            int inTree = -1;
            const UINT32 groupStart = nm;
            for (UINT32 ri = 0; ri < rs->count; ri++) {
                const Rule& r = rs->rules[ri];
                if (!MatchRuleLocked(s, r)) continue;
                if (inTree < 0) inTree = InTaskbarTreeLocked(s) ? 1 : 0;
                if (!inTree) break;
                UINT32 j = groupStart;
                while (j < nm && m[j].propId != r.propId) j++;
                if (j < nm) m[j].rule = (UINT16)ri;          // later rules win
                else if (nm < MAX_MATCHES) {
                    m[nm].element = s->key;
                    m[nm].propId = r.propId;
                    m[nm].rule = (UINT16)ri;
                    m[nm].pad = 0;
                    nm++;
                }
            }
        }
    }

    UINT32 i = 0;
    while (i < nm) {
        const UINT64 e = m[i].element;
        UINT32 j = i;
        while (j < nm && m[j].element == e) j++;

        bool need = false;
        {
            Locker l;
            for (UINT32 k = i; k < j; k++) {
                const Rule& r = rs->rules[m[k].rule];
                Record* rec = FindRecordLocked(e, m[k].propId);
                if (rec && rec->gen == cx.gen && rec->gen != 0 && rec->valueHash == r.valueHash) {
                    rec->pass = cx.pass;          // already applied from this rule set
                    m[k].rule = kMatchDone;
                } else {
                    need = true;
                }
            }
        }
        if (need) {
            Chain c;
            bool haveChain = ChainGet(cx.svc, e, &c);
            for (UINT32 k = i; k < j; k++) {
                if (m[k].rule == kMatchDone) continue;
                ApplyOne(cx, e, m[k].propId, rs->rules[m[k].rule], haveChain ? &c : NULL);
            }
            if (haveChain) ChainFree(&c);
        }
        i = j;
    }
    if (m) HeapFree(GetProcessHeap(), 0, m);

    UINT restored = RestoreStale(cx);
    if (restoredOut) *restoredOut = restored;

    UINT count = 0;
    {
        Locker l;
        for (UINT32 k = 0; k < g_recHigh; k++)
            if (g_recs[k].element && g_recs[k].tid == cx.tid && g_recs[k].pass == cx.pass && g_recs[k].gen != 0)
                count++;
    }
    ReleaseRules(rs);
    return count;
}

// Replaces the global rule set (rs may be NULL = no rules) and asks every other sink to
// re-apply on its own thread.
static void InstallRules(RuleSet* rs)
{
    RuleSet* old = NULL;
    HWND others[MAX_THREADS];
    DWORD otherTids[MAX_THREADS];
    int no = 0;
    const DWORD me = GetCurrentThreadId();
    {
        Locker l;
        old = g_rules;
        if (rs) {
            rs->refs = 1;   // the global reference
            if (++g_gen == 0) ++g_gen;
            rs->gen = g_gen;
        }
        g_rules = rs;
        for (int i = 0; i < MAX_THREADS; i++) {
            ThreadState& ts = g_threads[i];
            if (ts.tid && ts.tid != me && ts.sink && !ts.pendingPosted) {
                ts.pendingPosted = TRUE;
                others[no] = ts.sink;
                otherTids[no] = ts.tid;
                no++;
            }
        }
    }
    ReleaseRules(old);
    for (int i = 0; i < no; i++) {
        if (!PostMessageW(others[i], WM_OPAC_PENDING, 0, 0)) {
            Locker l;
            ThreadState* ts = FindThread(otherTids[i]);
            if (ts) ts->pendingPosted = FALSE;
        }
    }
}

// -------------------------------------------------------------------------------------
//  DUMP
// -------------------------------------------------------------------------------------
struct WBuf {
    WCHAR* p;
    SIZE_T len;
    SIZE_T cap;
    bool   bad;
};

static bool BufReserve(WBuf* b, SIZE_T extra)
{
    if (b->bad) return false;
    SIZE_T need = b->len + extra + 1;
    if (need > OPAC_MAX_DUMP_CHARS) { b->bad = true; return false; }
    if (need <= b->cap) return true;
    SIZE_T nc = b->cap ? b->cap * 2 : 65536;
    while (nc < need) nc *= 2;
    if (nc > OPAC_MAX_DUMP_CHARS) nc = OPAC_MAX_DUMP_CHARS;
    HANDLE heap = GetProcessHeap();
    WCHAR* np = b->p ? (WCHAR*)HeapReAlloc(heap, 0, b->p, nc * sizeof(WCHAR))
                     : (WCHAR*)HeapAlloc(heap, 0, nc * sizeof(WCHAR));
    if (!np) { b->bad = true; return false; }
    b->p = np;
    b->cap = nc;
    return true;
}

static void BufRaw(WBuf* b, const WCHAR* s)
{
    SIZE_T n = wcslen(s);
    if (!BufReserve(b, n)) return;
    CopyMemory(b->p + b->len, s, n * sizeof(WCHAR));
    b->len += n;
}

// Appends with tabs / line breaks replaced by spaces (keeps the line format intact).
static void BufText(WBuf* b, const WCHAR* s)
{
    if (!s) return;
    SIZE_T n = wcslen(s);
    if (n > 4096) n = 4096;
    if (!BufReserve(b, n)) return;
    for (SIZE_T i = 0; i < n; i++) {
        WCHAR ch = s[i];
        b->p[b->len++] = (ch == L'\t' || ch == L'\r' || ch == L'\n') ? L' ' : ch;
    }
}

static void BufUInt(WBuf* b, UINT64 v)
{
    WCHAR tmp[24];
    int n = 0;
    do { tmp[n++] = (WCHAR)(L'0' + (v % 10)); v /= 10; } while (v && n < 23);
    if (!BufReserve(b, (SIZE_T)n)) return;
    while (n > 0) b->p[b->len++] = tmp[--n];
}

static const WCHAR* ShortTypeName(const WCHAR* t)
{
    if (!t) return L"";
    const WCHAR* dot = wcsrchr(t, L'.');
    return dot ? dot + 1 : t;
}

// "Background or Fill": brush type, plus its Color for solid brushes.
static void BufBrush(WBuf* b, IVisualTreeService* svc, const Chain* c)
{
    int first, eff;
    ChainFind(c, L"Background", &first, &eff);
    if (first < 0) ChainFind(c, L"Fill", &first, &eff);
    int i = eff >= 0 ? eff : first;
    if (i < 0) return;
    const PropertyChainValue& v = c->vals[i];
    if (v.MetadataBits & IsValueHandle) {
        UINT64 h = ParseDecimal(v.Value);
        BufText(b, ShortTypeName(v.ValueType));
        if (h) {
            Chain bc;
            if (ChainGet(svc, h, &bc)) {
                const WCHAR* color = ChainValue(&bc, L"Color");
                if (color && *color) { BufRaw(b, L" "); BufText(b, color); }
                ChainFree(&bc);
            }
        }
    } else {
        BufText(b, v.Value);
    }
}

struct DumpNode {
    UINT64 key;
    UINT64 parent;
    UINT32 childIndex;
    UINT16 typeId;
    UINT16 nameId;
    BYTE   root;
    BYTE   visited;
    BYTE   pad[6];
};

static int __cdecl CmpDumpNode(const void* pa, const void* pb)
{
    const DumpNode* a = (const DumpNode*)pa;
    const DumpNode* b = (const DumpNode*)pb;
    if (a->parent != b->parent) return a->parent < b->parent ? -1 : 1;
    if (a->childIndex != b->childIndex) return a->childIndex < b->childIndex ? -1 : 1;
    if (a->key != b->key) return a->key < b->key ? -1 : 1;
    return 0;
}

static SIZE_T LowerBoundParent(const DumpNode* nodes, SIZE_T n, UINT64 parent)
{
    SIZE_T lo = 0, hi = n;
    while (lo < hi) {
        SIZE_T mid = lo + (hi - lo) / 2;
        if (nodes[mid].parent < parent) lo = mid + 1;
        else hi = mid;
    }
    return lo;
}

struct DumpFrame {
    SIZE_T node;
    UINT32 depth;
};

static LRESULT DumpTree(HWND target, HWND self)
{
    if (!target || !IsWindow(target)) return 0;
    IVisualTreeService* svc = NULL;
    const DWORD tid = GetCurrentThreadId();
    HANDLE heap = GetProcessHeap();
    DumpNode* nodes = NULL;
    SIZE_T n = 0;
    {
        Locker l;
        svc = g_service;
        SIZE_T count = 0;
        for (SIZE_T i = 0; i < g_mapCap; i++)
            if (g_map[i].used == 1 && g_map[i].tid == tid) count++;
        if (count) nodes = (DumpNode*)HeapAlloc(heap, HEAP_ZERO_MEMORY, count * sizeof(DumpNode));
        if (nodes) {
            for (SIZE_T i = 0; i < g_mapCap && n < count; i++) {
                const MapSlot* s = &g_map[i];
                if (s->used != 1 || s->tid != tid) continue;
                DumpNode& d = nodes[n++];
                d.key = s->key;
                d.parent = s->parent;
                d.childIndex = s->childIndex;
                d.typeId = s->typeId;
                d.nameId = s->nameId;
                d.root = ((s->flags & SLOT_ROOT) && !HasMarkedAncestorLocked(s)) ? 1 : 0;
            }
        }
    }
    if (!svc) { if (nodes) HeapFree(heap, 0, nodes); return 0; }

    WBuf b;
    ZeroMemory(&b, sizeof(b));
    BufRaw(&b, L"opacbar-tree 2\n");

    if (n) {
        qsort(nodes, n, sizeof(DumpNode), CmpDumpNode);
        DumpFrame* stack = (DumpFrame*)HeapAlloc(heap, 0, (2 * n + 1) * sizeof(DumpFrame));
        const SIZE_T stackCap = 2 * n + 1;
        UINT lines = 0;
        bool stop = (stack == NULL);
        for (SIZE_T r = 0; r < n && !stop; r++) {
            if (!nodes[r].root || nodes[r].visited) continue;
            SIZE_T sp = 0;
            stack[sp].node = r; stack[sp].depth = 0; sp++;
            while (sp > 0 && !stop) {
                DumpFrame f = stack[--sp];
                DumpNode& d = nodes[f.node];
                if (d.visited) continue;
                d.visited = 1;

                const SIZE_T mark = b.len;
                BufUInt(&b, f.depth);                       BufRaw(&b, L"\t");
                BufText(&b, PoolStr(d.typeId));             BufRaw(&b, L"\t");
                BufText(&b, PoolStr(d.nameId));             BufRaw(&b, L"\t");
                BufUInt(&b, d.childIndex);                  BufRaw(&b, L"\t");
                Chain c;
                if (ChainGet(svc, d.key, &c)) {
                    const WCHAR* w = ChainValue(&c, L"ActualWidth");
                    const WCHAR* h = ChainValue(&c, L"ActualHeight");
                    if ((w && *w) || (h && *h)) { BufText(&b, w); BufRaw(&b, L"x"); BufText(&b, h); }
                    BufRaw(&b, L"\t"); BufText(&b, ChainValue(&c, L"Margin"));
                    BufRaw(&b, L"\t"); BufText(&b, ChainValue(&c, L"Padding"));
                    BufRaw(&b, L"\t"); BufText(&b, ChainValue(&c, L"CornerRadius"));
                    BufRaw(&b, L"\t"); BufBrush(&b, svc, &c);
                    ChainFree(&c);
                } else {
                    BufRaw(&b, L"\t\t\t\t");
                }
                BufRaw(&b, L"\n");
                if (b.bad) {          // out of space: drop the partial line and stop
                    b.len = mark;
                    stop = true;
                    break;
                }
                if (++lines >= OPAC_MAX_DUMP_LINES) { stop = true; break; }

                // Push children in reverse so they come out in child-index order.
                SIZE_T lo = LowerBoundParent(nodes, n, d.key);
                SIZE_T hi = lo;
                while (hi < n && nodes[hi].parent == d.key) hi++;
                for (SIZE_T k = hi; k > lo; k--) {
                    if (sp >= stackCap) break;
                    if (nodes[k - 1].visited) continue;
                    stack[sp].node = k - 1;
                    stack[sp].depth = f.depth + 1;
                    sp++;
                }
            }
        }
        if (stack) HeapFree(heap, 0, stack);
    }
    if (nodes) HeapFree(heap, 0, nodes);

    LRESULT result = 0;
    if (b.p && b.len > 0) {
        COPYDATASTRUCT cds;
        cds.dwData = OPAC_COPYDATA_TREE;
        cds.cbData = (DWORD)(b.len * sizeof(WCHAR));
        cds.lpData = b.p;
        DWORD_PTR res = 0;
        if (SendMessageTimeoutW(target, WM_COPYDATA, (WPARAM)self, (LPARAM)&cds,
                                SMTO_ABORTIFHUNG, 10000, &res))
            result = 1;
    }
    if (b.p) HeapFree(heap, 0, b.p);
    return result;
}

// -------------------------------------------------------------------------------------
//  Sink window (one per taskbar UI thread)
// -------------------------------------------------------------------------------------
static void ForgetThread(DWORD tid, HWND hwnd)
{
    Locker l;
    ThreadState* ts = FindThread(tid);
    if (hwnd == g_controlSink) g_controlSink = NULL;
    if (!ts || ts->sink != hwnd) return;
    DropThreadRecordsLocked(tid);
    ZeroMemory(ts, sizeof(ThreadState));
}

// STATS: low 16 bits = elements known on this thread, bits 16..31 = of those, elements in
// taskbar trees (both saturate at 0xFFFF). On the control sink the counts are process-wide.
// 64-bit builds add state bits: 32 advise ok, 33 advise failed, 34 advise in progress,
// 35 this is the control sink, 36 unloading.
static LRESULT GetStats(HWND self)
{
    const DWORD tid = GetCurrentThreadId();
    UINT64 known = 0, tree = 0;
    bool isControl;
    {
        Locker l;
        isControl = (self == g_controlSink);
        for (SIZE_T i = 0; i < g_mapCap; i++) {
            const MapSlot* s = &g_map[i];
            if (s->used != 1 || (!isControl && s->tid != tid)) continue;
            known++;
            if (InTaskbarTreeLocked(s)) tree++;
        }
    }
    if (known > 0xFFFF) known = 0xFFFF;
    if (tree > 0xFFFF) tree = 0xFFFF;
    UINT64 v = known | (tree << 16);
#if defined(_WIN64)
    const LONG st = g_adviseState;
    if (st == 2) v |= (UINT64)1 << 32;
    if (st == 3) v |= (UINT64)1 << 33;
    if (st == 1) v |= (UINT64)1 << 34;
    if (isControl) v |= (UINT64)1 << 35;
    if (g_unloading) v |= (UINT64)1 << 36;
#endif
    return (LRESULT)v;
}

// UNLOAD (received by any sink): restore every thread synchronously, destroy the sinks,
// then let the control thread unsubscribe and release everything.
static LRESULT BeginUnload(HWND self)
{
    HWND others[MAX_THREADS];
    int no = 0;
    HWND ctrl = NULL;
    {
        Locker l;
        if (g_unloading || g_adviseState != 2 || !g_controlSink || !g_callback || !g_service)
            return 0;   // not in a state we can safely unwind
        g_unloading = 1;
        ctrl = g_controlSink;
        for (int i = 0; i < MAX_THREADS; i++) {
            HWND s = g_threads[i].sink;
            if (g_threads[i].tid && s && s != self && s != ctrl) others[no++] = s;
        }
    }
    InstallRules(NULL);
    UINT restored = 0;
    ApplyRulesOnThisThread(&restored);
    for (int i = 0; i < no; i++) {
        DWORD_PTR res = 0;
        SendMessageTimeoutW(others[i], WM_OPAC_SHUTDOWN, 0, 0, SMTO_ABORTIFHUNG, 2000, &res);
    }
    if (self != ctrl) PostMessageW(self, WM_OPAC_SHUTDOWN, 0, 0);   // destroy ourselves later
    PostMessageW(ctrl, WM_OPAC_UNLOAD_RUN, 0, 0);
    return 1;
}

static void AppendW(WCHAR* buf, SIZE_T cap, const WCHAR* s)
{
    wcscat_s(buf, cap, s);
}

static LRESULT HandleRulesDoc(const WCHAR* data, SIZE_T nChars)
{
    bool headerOk = false;
    RuleSet* rs = ParseRules(data, nChars, &headerOk);
    if (!rs) return headerOk ? 0 : -1;
    InstallRules(rs);
    return (LRESULT)ApplyRulesOnThisThread(NULL);
}

static LRESULT SinkWndProcImpl(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

// No C++ exception may leave a window procedure (it would unwind through user32/Explorer).
static LRESULT CALLBACK SinkWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    try {
        return SinkWndProcImpl(hwnd, msg, wParam, lParam);
    } catch (...) {
        OutputDebugStringW(L"[OpacbarTAP] exception caught in window procedure\n");
        return 0;
    }
}

static LRESULT SinkWndProcImpl(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (g_unloading) {
        // Inert: never (re)apply anything once an unload started.
        if (msg == WM_COPYDATA || msg == WM_OPAC_SET || msg == WM_OPAC_PENDING || msg == WM_OPAC_DUMP)
            return 0;
    }
    switch (msg) {
    case WM_OPAC_STATS:
        return GetStats(hwnd);
    case WM_OPAC_UNLOAD:
        return BeginUnload(hwnd);
    case WM_OPAC_SHUTDOWN: {
        if (!g_unloading) return 0;
        UINT restored = 0;
        ApplyRulesOnThisThread(&restored);   // rule set is empty: restores this thread
        HWND ctrl;
        {
            Locker l;
            ctrl = g_controlSink;
        }
        if (hwnd != ctrl) DestroyWindow(hwnd);
        return 1;
    }
    case WM_OPAC_UNLOAD_RUN: {
        HWND ctrl;
        {
            Locker l;
            ctrl = g_controlSink;
        }
        if (hwnd == ctrl && g_unloading) {
            InterlockedExchange(&g_unloadRun, 1);
            PostQuitMessage(0);   // ControlThread finishes the unload after its loop
        }
        return 0;
    }
    case WM_COPYDATA: {
        const COPYDATASTRUCT* cds = (const COPYDATASTRUCT*)lParam;
        if (!cds || cds->dwData != OPAC_COPYDATA_RULES) return -1;
        if (cds->cbData > OPAC_MAX_DOC_BYTES || (cds->cbData && !cds->lpData)) return -1;
        return HandleRulesDoc((const WCHAR*)cds->lpData, cds->cbData / sizeof(WCHAR));
    }
    case WM_OPAC_SET: {
        // v1 compatibility: expressed as an equivalent rules document.
        WCHAR doc[512];
        doc[0] = 0;
        AppendW(doc, ARRAYSIZE(doc), L"opacbar-rules 2\n");
        if (lParam & OPAC_FLAG_ON) {
            static const WCHAR hex[] = L"0123456789ABCDEF";
            const UINT32 argb = (UINT32)(wParam & 0xFFFFFFFFu);
            WCHAR color[10];
            color[0] = L'#';
            for (int i = 0; i < 8; i++) color[1 + i] = hex[(argb >> (28 - 4 * i)) & 0xF];
            color[9] = 0;
            AppendW(doc, ARRAYSIZE(doc), L"Taskbar.TaskbarBackground > *#BackgroundFill\tFill\t");
            AppendW(doc, ARRAYSIZE(doc), color);
            AppendW(doc, ARRAYSIZE(doc), L"\n");
            if (lParam & OPAC_FLAG_HIDE_STROKE)
                AppendW(doc, ARRAYSIZE(doc), L"Taskbar.TaskbarBackground > *#BackgroundStroke\tFill\t#00000000\n");
        }
        LRESULT r = HandleRulesDoc(doc, wcslen(doc));
        return r < 0 ? 0 : r;
    }
    case WM_OPAC_RESTORE: {
        InstallRules(NULL);
        UINT restored = 0;
        ApplyRulesOnThisThread(&restored);
        return (LRESULT)restored;
    }
    case WM_OPAC_PING:
        return OPAC_PING_REPLY;
    case WM_OPAC_VERSION:
        return OPAC_PROTOCOL_VERSION;
    case WM_OPAC_DUMP:
        return DumpTree((HWND)wParam, hwnd);
    case WM_OPAC_PENDING:
        ApplyRulesOnThisThread(NULL);
        return 0;
    case WM_NCDESTROY:
        ForgetThread(GetCurrentThreadId(), hwnd);
        break;
    }
    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

static bool RegisterSinkClass()
{
    if (g_classOk) return true;
    WNDCLASSEXW wc;
    ZeroMemory(&wc, sizeof(wc));
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = SinkWndProc;
    wc.hInstance = g_module;
    wc.lpszClassName = OPAC_SINK_CLASS;
    if (RegisterClassExW(&wc) || GetLastError() == ERROR_CLASS_ALREADY_EXISTS) {
        InterlockedExchange(&g_classOk, 1);
        return true;
    }
    return false;
}

// Must be called on the UI thread `tid` (i.e. from the tree-change callback).
static HWND EnsureSink(DWORD tid)
{
    HWND existing = NULL;
    {
        Locker l;
        ThreadState* ts = FindThread(tid);
        if (ts) existing = ts->sink;
    }
    if (existing && IsWindow(existing) && GetWindowThreadProcessId(existing, NULL) == tid)
        return existing;

    if (!RegisterSinkClass()) return NULL;
    HWND hwnd = CreateWindowExW(0, OPAC_SINK_CLASS, L"OpacbarTAP", 0, 0, 0, 0, 0,
                                HWND_MESSAGE, NULL, g_module, NULL);
    if (!hwnd) return NULL;

    bool stored = false;
    {
        Locker l;
        ThreadState* ts = FindOrAddThread(tid);
        if (ts) {
            if (ts->sink != NULL) {
                // Old sink from a dead thread whose id got reused: start fresh.
                DropThreadRecordsLocked(tid);
                ZeroMemory(ts->cache, sizeof(ts->cache));
                ts->nextEvict = 0;
                ts->pendingPosted = FALSE;
            }
            ts->sink = hwnd;
            stored = true;
        }
    }
    if (!stored) { DestroyWindow(hwnd); return NULL; }
    return hwnd;
}

// Frees our heap state. Only called after an unload when no callback, sink or apply
// pass can touch it any more.
static void FreeAllStateLocked()
{
    HANDLE heap = GetProcessHeap();
    for (UINT32 i = 0; i < g_recHigh; i++) {
        SysFreeString(g_recs[i].origType);
        SysFreeString(g_recs[i].origValue);
        ZeroMemory(&g_recs[i], sizeof(Record));   // XAML objects (if any) deliberately leaked
    }
    g_recHigh = 0;
    if (g_map) HeapFree(heap, 0, g_map);
    g_map = NULL;
    g_mapCap = g_mapLive = g_mapUsed = 0;
    if (g_poolStr) {
        for (UINT32 i = 1; i < g_poolCount && i < POOL_MAX_STRINGS; i++)
            if (g_poolStr[i]) HeapFree(heap, 0, g_poolStr[i]);
        HeapFree(heap, 0, g_poolStr);
    }
    if (g_poolHash) HeapFree(heap, 0, g_poolHash);
    g_poolStr = NULL;
    g_poolHash = NULL;
    g_poolCount = 1;
    g_poolChars = 0;
}

// Runs on the control thread after its message loop ended because of an UNLOAD.
// Returns true if the module reference may be dropped.
static bool FinishUnload()
{
    IVisualTreeService* svc = NULL;
    IVisualTreeServiceCallback2* cb = NULL;
    HWND ctrl = NULL;
    {
        Locker l;
        svc = g_service;
        cb = g_callback;
        ctrl = g_controlSink;
    }
    if (ctrl) DestroyWindow(ctrl);   // our own thread's window
    if (!svc || !cb) return false;

    bool safe = SUCCEEDED(svc->UnadviseVisualTreeChange(static_cast<IVisualTreeServiceCallback*>(cb)));

    // Wait for callbacks that were already running.
    for (int i = 0; i < 500 && g_inCallback != 0; i++) Sleep(10);
    if (g_inCallback != 0) safe = false;

    // Wait for every sink to be gone (they destroy themselves on WM_OPAC_SHUTDOWN).
    for (int i = 0; i < 300; i++) {
        bool any = false;
        {
            Locker l;
            for (int t = 0; t < MAX_THREADS; t++)
                if (g_threads[t].sink && IsWindow(g_threads[t].sink)) any = true;
        }
        if (!any) break;
        if (i == 299) safe = false;
        Sleep(10);
    }
    if (!safe) return false;   // stay loaded and inert; nothing released

    IXamlDiagnostics* diag = NULL;
    {
        Locker l;
        g_callback = NULL;
        g_service = NULL;
        diag = g_diag;
        g_diag = NULL;
        RuleSet* rs = g_rules;   // InstallRules(NULL) already ran; be safe anyway
        g_rules = NULL;
        if (rs && --rs->refs == 0) FreeRuleSet(rs);
        ZeroMemory(g_threads, sizeof(g_threads));
        FreeAllStateLocked();
    }
    cb->Release();
    svc->Release();
    if (diag) diag->Release();
    if (UnregisterClassW(OPAC_SINK_CLASS, g_module)) InterlockedExchange(&g_classOk, 0);
    InterlockedExchange(&g_advised, 0);
    return true;
}

// Our own thread with a sink window: shows up even if no XAML callback ever arrives
// (field diagnostics via STATS) and performs UNLOAD off the UI threads.
static DWORD WINAPI ControlThread(LPVOID param)
{
    UNREFERENCED_PARAMETER(param);
    HRESULT hrInit = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    HWND sink = EnsureSink(GetCurrentThreadId());
    if (sink) {
        {
            Locker l;
            g_controlSink = sink;
        }
        MSG msg;
        while (GetMessageW(&msg, NULL, 0, 0) > 0) DispatchMessageW(&msg);
    }
    bool release = false;
    if (g_unloadRun) release = FinishUnload();
    if (SUCCEEDED(hrInit)) CoUninitialize();
    // Drop our module reference only when Explorer holds no object of ours any more;
    // otherwise the code stays mapped (inert) until Explorer exits.
    // Blur brushes / effect nodes are our code too: give XAML a moment to drop them.
    for (int i = 0; release && i < 200 && opacfx::LiveObjects() != 0; i++) Sleep(10);
    if (release && g_selfRef && g_objects == 0 && g_locks == 0 && opacfx::LiveObjects() == 0) {
        InterlockedExchange(&g_selfRef, 0);
        Sleep(100);   // let any callback epilogue that decremented g_inCallback return
        FreeLibraryAndExitThread(g_module, 0);
    }
    return 0;
}

// -------------------------------------------------------------------------------------
//  The TAP object: IObjectWithSite (entry point) + IVisualTreeServiceCallback2 (events)
// -------------------------------------------------------------------------------------
struct AdviseArgs {
    IVisualTreeService*          svc;
    IVisualTreeServiceCallback2* cb;   // holds one reference, released by the thread
};

static DWORD WINAPI AdviseThread(LPVOID param)
{
    AdviseArgs* a = (AdviseArgs*)param;
    HRESULT hrInit = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    // Must not run on the SetSite thread: Advise synchronously walks every UI thread's
    // tree and would deadlock there.
    OutputDebugStringW(L"[OpacbarTAP] Advise starting\n");
    HRESULT hr = a->svc->AdviseVisualTreeChange(static_cast<IVisualTreeServiceCallback*>(a->cb));
    OutputDebugStringW(SUCCEEDED(hr) ? L"[OpacbarTAP] Advise ok\n" : L"[OpacbarTAP] Advise FAILED\n");
    if (SUCCEEDED(hr)) {
        {
            Locker l;
            g_callback = a->cb;   // keep this reference: needed for UnadviseVisualTreeChange
        }
        InterlockedExchange(&g_adviseState, 2);
    } else {
        a->cb->Release();
        InterlockedExchange(&g_adviseState, 3);
        InterlockedExchange(&g_advised, 0);   // allow a later injection to retry
    }
    HeapFree(GetProcessHeap(), 0, a);
    if (SUCCEEDED(hrInit)) CoUninitialize();
    return 0;
}

class TapObject : public IObjectWithSite, public IVisualTreeServiceCallback2 {
public:
    TapObject() : m_ref(1), m_site(NULL) { InterlockedIncrement(&g_objects); }

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;
        if (IsEqualGUID(riid, kIID_IUnknown) || IsEqualGUID(riid, kIID_IObjectWithSite))
            *ppv = static_cast<IObjectWithSite*>(this);
        else if (IsEqualGUID(riid, IID_IVisualTreeServiceCallback) ||
                 IsEqualGUID(riid, IID_IVisualTreeServiceCallback2))
            *ppv = static_cast<IVisualTreeServiceCallback2*>(this);
        else
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&m_ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return (ULONG)r;
    }

    // IObjectWithSite (no C++ exception may cross this COM boundary)
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override
    {
        try {
            return SetSiteImpl(site);
        } catch (...) {
            return E_UNEXPECTED;
        }
    }

    HRESULT SetSiteImpl(IUnknown* site)
    {
        IUnknown* old = NULL;
        {
            Locker l;
            old = m_site;
            m_site = site;
            if (site) site->AddRef();
        }
        if (old) old->Release();
        OutputDebugStringW(site ? L"[OpacbarTAP] SetSite called\n" : L"[OpacbarTAP] SetSite(NULL)\n");
        if (!site) return S_OK;
        if (g_unloading) return S_OK;   // this copy was unloaded: stay inert

        // IXamlDiagnostics (needed for XAML values and as the route to the tree service).
        IXamlDiagnostics* diag = NULL;
        if (FAILED(site->QueryInterface(IID_IXamlDiagnostics, (void**)&diag))) diag = NULL;
        OutputDebugStringW(diag ? L"[OpacbarTAP] IXamlDiagnostics ok\n" : L"[OpacbarTAP] IXamlDiagnostics QI FAILED\n");
        if (diag) {
            {
                Locker l;
                if (!g_diag) { g_diag = diag; diag = NULL; }   // keep this reference forever
            }
            if (diag) diag->Release();
        }

        // The tree service is reached through IXamlDiagnostics (as TranslucentTB's TAP does);
        // asking the raw site directly fails on some builds and silently disabled the helper.
        IVisualTreeService* svc = NULL;
        HRESULT hr = E_NOINTERFACE;
        {
            IXamlDiagnostics* d = NULL;
            {
                Locker l;
                d = g_diag;
                if (d) d->AddRef();
            }
            if (d) {
                hr = d->QueryInterface(IID_IVisualTreeService, (void**)&svc);
                d->Release();
            }
        }
        if (FAILED(hr) || !svc) {
            svc = NULL;
            hr = site->QueryInterface(IID_IVisualTreeService, (void**)&svc);
        }
        OutputDebugStringW(SUCCEEDED(hr) && svc ? L"[OpacbarTAP] SetSite: tree service ok\n"
                                                : L"[OpacbarTAP] SetSite: tree service QI FAILED\n");
        if (FAILED(hr)) return hr;
        if (!svc) return E_NOINTERFACE;

        IVisualTreeService* use = NULL;
        {
            Locker l;
            if (!g_service) { g_service = svc; svc = NULL; }   // keep this reference forever
            use = g_service;
        }
        if (svc) svc->Release();

        // Only one subscription per Explorer process, even if Opacbar injects again.
        if (InterlockedCompareExchange(&g_advised, 1, 0) != 0) return S_OK;

        // Callbacks + window procs + our threads live in this DLL: hold a module reference
        // until a completed UNLOAD (never released otherwise).
        if (InterlockedCompareExchange(&g_selfRef, 1, 0) == 0 && !TakeModuleRef())
            InterlockedExchange(&g_selfRef, 0);

        InterlockedExchange(&g_adviseState, 1);
        AdviseArgs* a = (AdviseArgs*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(AdviseArgs));
        if (!a) {
            InterlockedExchange(&g_adviseState, 3);
            InterlockedExchange(&g_advised, 0);
            return E_OUTOFMEMORY;
        }
        a->svc = use;
        a->cb = static_cast<IVisualTreeServiceCallback2*>(this);
        AddRef();

        HANDLE th = CreateThread(NULL, 0, AdviseThread, a, 0, NULL);
        if (!th) {
            DWORD err = GetLastError();
            HeapFree(GetProcessHeap(), 0, a);
            Release();
            InterlockedExchange(&g_adviseState, 3);
            InterlockedExchange(&g_advised, 0);
            return HRESULT_FROM_WIN32(err ? err : ERROR_GEN_FAILURE);
        }
        CloseHandle(th);

        // Control thread (once per process): diagnostics sink + unload worker.
        static volatile LONG s_controlStarted = 0;
        if (InterlockedCompareExchange(&s_controlStarted, 1, 0) == 0) {
            HANDLE ct = CreateThread(NULL, 0, ControlThread, NULL, 0, NULL);
            if (ct) CloseHandle(ct);
            else InterlockedExchange(&s_controlStarted, 0);
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;
        IUnknown* site = NULL;
        {
            Locker l;
            site = m_site;
            if (site) site->AddRef();
        }
        if (!site) return E_FAIL;
        HRESULT hr = site->QueryInterface(riid, ppv);
        site->Release();
        return hr;
    }

    // IVisualTreeServiceCallback - runs on the UI thread that owns `element`.
    // The BSTRs inside `element` are [in] parameters owned by the caller: never freed here.
    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation relation, VisualElement element, VisualMutationType mutationType) override
    {
        InterlockedIncrement(&g_inCallback);
        try {
            if (!g_unloading) TreeChange(relation, element, mutationType);
        } catch (...) {
            // never let an exception cross into the XAML runtime
        }
        InterlockedDecrement(&g_inCallback);
        return S_OK;
    }

    // IVisualTreeServiceCallback2
    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle element, VisualElementState elementState, LPCWSTR context) override
    {
        UNREFERENCED_PARAMETER(element);
        UNREFERENCED_PARAMETER(elementState);
        UNREFERENCED_PARAMETER(context);
        return S_OK;
    }

private:
    static void TreeChange(const ParentChildRelation& relation, const VisualElement& element,
                           VisualMutationType mutationType)
    {
        const DWORD tid = GetCurrentThreadId();

        // One sink per XAML UI thread, created at the first callback seen on that thread
        // (not gated on recognizing the taskbar, so the exe can always find and query it).
        if (g_tlsIndex != TLS_OUT_OF_INDEXES) {
            if (TlsGetValue(g_tlsIndex) == NULL) {
                HWND s = EnsureSink(tid);
                TlsSetValue(g_tlsIndex, s ? (LPVOID)s : (LPVOID)(ULONG_PTR)1);
            }
        } else {
            EnsureSink(tid);
        }

        UINT64 handle = element.Handle ? element.Handle : relation.Child;
        if (handle == 0) return;

        if (mutationType == Add) {
            const bool isFrame = IsFrameType(element.Type);
            if (isFrame) EnsureSink(tid);   // creates the window outside the lock

            HWND post = NULL;
            {
                Locker l;
                // Bounded wcsnlen instead of SysStringLen: don't trust a BSTR length prefix.
                UINT16 typeId = InternLocked(element.Type, element.Type ? wcsnlen(element.Type, POOL_MAX_LEN + 1) : 0);
                UINT16 nameId = InternLocked(element.Name, element.Name ? wcsnlen(element.Name, POOL_MAX_LEN + 1) : 0);
                MapPut(handle, relation.Parent, tid, relation.ChildIndex, typeId, nameId);
                if (isFrame) MarkRootLocked(handle);
                if (g_rules && g_rules->count) {
                    ThreadState* ts = FindThread(tid);
                    if (ts && ts->sink && !ts->pendingPosted) {
                        ts->pendingPosted = TRUE;
                        post = ts->sink;
                    }
                }
            }
            // Don't touch XAML from inside the callback: defer to our window proc.
            if (post && !PostMessageW(post, WM_OPAC_PENDING, 0, 0)) {
                Locker l;
                ThreadState* ts = FindThread(tid);
                if (ts) ts->pendingPosted = FALSE;
            }
        } else if (mutationType == Remove) {
            // Forget applied records on the removed element or below it (no restore: the
            // element is leaving the tree), in batches so XAML objects are released
            // outside the lock.
            for (;;) {
                Record taken[16];
                UINT nt = 0;
                {
                    Locker l;
                    for (UINT32 i = 0; i < g_recHigh && nt < 16; i++) {
                        Record& r = g_recs[i];
                        if (r.element && IsUnderLocked(r.element, handle)) {
                            taken[nt++] = r;
                            ZeroMemory(&r, sizeof(Record));
                        }
                    }
                }
                for (UINT i = 0; i < nt; i++) FreeRecordResources(taken[i], true);
                if (nt < 16) break;
            }
            Locker l;
            MapEraseSubtree(handle);
        }
    }

    ~TapObject()
    {
        if (m_site) m_site->Release();
        InterlockedDecrement(&g_objects);
    }

    volatile LONG m_ref;
    IUnknown*     m_site;
};

// -------------------------------------------------------------------------------------
//  Class factory + DLL exports
// -------------------------------------------------------------------------------------
class TapFactory : public IClassFactory {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;
        if (IsEqualGUID(riid, kIID_IUnknown) || IsEqualGUID(riid, kIID_IClassFactory)) {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override  { return 2; }   // static object
    ULONG STDMETHODCALLTYPE Release() override { return 1; }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;
        if (outer) return CLASS_E_NOAGGREGATION;
        TapObject* obj = new (std::nothrow) TapObject();
        if (!obj) return E_OUTOFMEMORY;
        HRESULT hr = obj->QueryInterface(riid, ppv);
        OutputDebugStringW(SUCCEEDED(hr) ? L"[OpacbarTAP] CreateInstance ok\n" : L"[OpacbarTAP] CreateInstance: QI FAILED\n");
        obj->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_locks);
        else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static TapFactory g_factory;

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    OutputDebugStringW(L"[OpacbarTAP] DllGetClassObject\n");
    if (!ppv) return E_POINTER;
    *ppv = NULL;
    if (!IsEqualGUID(rclsid, kCLSID_OpacbarTap)) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    // While subscribed (or while Explorer holds any object of ours) we stay loaded.
    if (g_advised || g_selfRef || g_objects > 0 || g_locks > 0 || opacfx::LiveObjects() != 0) return S_FALSE;
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
        InitializeCriticalSection(&g_lock);
        g_tlsIndex = TlsAlloc();   // may be TLS_OUT_OF_INDEXES: handled (slower path)
    } else if (reason == DLL_PROCESS_DETACH && reserved == NULL) {
        // Real FreeLibrary (only after a completed UNLOAD), not process exit.
        if (g_tlsIndex != TLS_OUT_OF_INDEXES) TlsFree(g_tlsIndex);
        g_tlsIndex = TLS_OUT_OF_INDEXES;
        DeleteCriticalSection(&g_lock);
    }
    return TRUE;
}
