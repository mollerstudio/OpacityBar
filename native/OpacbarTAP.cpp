// =====================================================================================
//  OpacbarTAP.cpp - the small helper that Opacbar loads into explorer.exe
// =====================================================================================
//
//  PLAIN-ENGLISH SUMMARY (read this first if you are auditing Opacbar)
//  -------------------------------------------------------------------
//  Windows 11 draws its taskbar with XAML. The colored "slab" behind your taskbar icons
//  is a XAML Rectangle named "BackgroundFill" (plus a 1px line on top named
//  "BackgroundStroke"), both inside a control called "TaskbarBackground". A normal app
//  cannot reach into Explorer's XAML, so Opacbar uses Microsoft's official, documented
//  XAML Diagnostics API (InitializeXamlDiagnosticsEx - the same API Visual Studio's Live
//  Visual Tree and TranslucentTB use) to load this DLL into explorer.exe. This file is
//  the complete source of that DLL.
//
//  WHAT IT DOES
//    1. Explorer's XAML runtime creates our COM object and hands it the "visual tree
//       service" (IVisualTreeService).
//    2. We subscribe to visual-tree change notifications. For every XAML element Explorer
//       creates or removes we remember only: its handle (a number), its parent's handle
//       (a number) and one bit saying "this is inside TaskbarBackground". We do NOT keep
//       element names, text, or any content.
//    3. When we see a Rectangle named BackgroundFill / BackgroundStroke inside
//       TaskbarBackground, we remember it and create one invisible, message-only window
//       (class "OpacbarTAP.Sink") on that taskbar's UI thread.
//    4. Opacbar.exe finds those windows and sends them three messages:
//         SET     - paint the background rectangle with a given ARGB color
//                   (and optionally make the top stroke line transparent)
//         RESTORE - put the original Windows brush back
//         PING    - "are you alive?" (answers 0x0BAC)
//    5. To paint, we ask the XAML runtime to create a SolidColorBrush from a string such as
//       "#00000000" and assign it to the rectangle's Fill property. That is all.
//       Because only the background rectangle is touched, taskbar icons, text and the
//       clock stay 100% opaque, and a fully transparent (alpha 0) background is possible.
//
//  WHAT IT DOES NOT DO
//    - No network access of any kind (no sockets, no HTTP, no WinINet/WinHTTP).
//    - No file or registry reads or writes. No settings stored here at all.
//    - No keyboard or mouse hooks, no keylogging, no screen capture, no clipboard access.
//    - Does not read, store or send any text, names or content shown by Explorer.
//    - Does not touch any XAML element other than the two taskbar background rectangles.
//    - Does not start other processes, load other DLLs, or elevate privileges.
//    - Talks only to whoever sends the three window messages above; the worst any other
//      program could do through them is change the taskbar background color.
//
//  SAFETY RULES THIS FILE FOLLOWS (a crash here would crash Explorer)
//    - Every HRESULT is checked; failures are skipped, never fatal.
//    - No C++ exceptions are used or thrown (allocation uses nothrow / HeapAlloc).
//    - All shared state is guarded by one critical section, and the lock is NEVER held
//      while calling into XAML or creating windows.
//    - Inside the tree-change callback we only update bookkeeping and post a message;
//      the actual property changes happen later from our own window procedure.
//    - The DLL pins itself in memory once it has subscribed, so Explorer never unloads
//      code that still has live callbacks or window procedures.
//    - The XAML Diagnostics interfaces below are hand-declared (no SDK xamlom.h needed).
//      Their IIDs, struct layouts and vtable order were checked against xamlom.idl as
//      published in the Windows SDK / mingw-w64 headers. Do not reorder them.
//
//  Build (MSVC, from a VS developer prompt):
//    cl /nologo /LD /O2 /EHs-c- /GS /W4 /DUNICODE /D_UNICODE native\OpacbarTAP.cpp ^
//       /Fe:OpacbarTAP.x64.dll /link /DEF:native\OpacbarTAP.def ole32.lib oleaut32.lib user32.lib
//
//  License: MIT (see LICENSE in the repository root).
// =====================================================================================

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <ole2.h>
#include <oleauto.h>
#include <ocidl.h>
#include <wchar.h>
#include <new>

// -------------------------------------------------------------------------------------
//  IPC contract with Opacbar.exe (keep in sync with src/TapInjector.cs)
// -------------------------------------------------------------------------------------
#define OPAC_SINK_CLASS   L"OpacbarTAP.Sink"
#define WM_OPAC_SET       (WM_APP + 0x10)  // wParam = 0xAARRGGBB, lParam = flags. Returns #elements updated.
#define WM_OPAC_RESTORE   (WM_APP + 0x11)  // Restores the original brushes. Returns #elements restored.
#define WM_OPAC_PING      (WM_APP + 0x12)  // Returns OPAC_PING_REPLY.
#define WM_OPAC_PENDING   (WM_APP + 0x13)  // Internal: apply the last SET state to newly found elements.
#define OPAC_PING_REPLY   0x0BAC
#define OPAC_FLAG_ON          0x1          // bit0: override on (0 = restore Windows defaults)
#define OPAC_FLAG_HIDE_STROKE 0x2          // bit1: make BackgroundStroke transparent too

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

} // namespace xamlom

using namespace xamlom;

// -------------------------------------------------------------------------------------
//  Global state (all guarded by g_lock)
// -------------------------------------------------------------------------------------
static HMODULE            g_module   = NULL;
static CRITICAL_SECTION   g_lock;
static volatile LONG      g_objects  = 0;   // live TapObject instances
static volatile LONG      g_locks    = 0;   // IClassFactory::LockServer count
static volatile LONG      g_advised  = 0;   // 1 once AdviseVisualTreeChange is started/succeeded
static volatile LONG      g_classOk  = 0;   // sink window class registered
static IVisualTreeService* g_service = NULL; // kept for the life of the process once obtained

struct Locker {
    Locker()  { EnterCriticalSection(&g_lock); }
    ~Locker() { LeaveCriticalSection(&g_lock); }
};

// Last state requested by Opacbar.exe (applied to newly discovered elements too).
struct OpacState {
    UINT32 argb;
    bool   overrideOn;
    bool   hideStroke;
};
static OpacState g_state = { 0, false, false };

// ---- Element map: handle -> { parent, flags } (open addressing, no STL, no exceptions) ----
#define NODE_IN_BG 0x1   // element is TaskbarBackground or one of its descendants

struct MapSlot {
    UINT64 key;
    UINT64 parent;
    UINT32 flags;
    UINT32 used;      // 0 = empty, 1 = live, 2 = deleted (tombstone)
};
static MapSlot* g_map      = NULL;
static SIZE_T   g_mapCap   = 0;    // power of two
static SIZE_T   g_mapLive  = 0;
static SIZE_T   g_mapUsed  = 0;    // live + tombstones
static const SIZE_T kMapMaxCap = (SIZE_T)1 << 18;   // 262k slots (~6 MB) hard ceiling

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

static void MapPut(UINT64 key, UINT64 parent, UINT32 flags)
{
    if (key == 0) return;
    MapSlot* existing = MapFind(key);
    if (existing) { existing->parent = parent; existing->flags = flags; return; }

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
            s->key = key; s->parent = parent; s->flags = flags; s->used = 1;
            g_mapLive++;
            return;
        }
        i = (i + 1) & mask;
    }
    // Table completely full (only possible at kMapMaxCap): silently drop.
}

static void MapErase(UINT64 key)
{
    MapSlot* s = MapFind(key);
    if (!s) return;
    s->used = 2;
    s->key = 0;
    g_mapLive--;
}

// ---- Target rectangles ----
#define KIND_FILL   0
#define KIND_STROKE 1
#define MAX_TARGETS 64

struct Target {
    UINT64 handle;      // 0 = free slot
    UINT64 original;    // original local Fill brush handle (0 = none / use ClearProperty)
    DWORD  tid;         // UI thread that owns the element
    UINT32 fillIndex;   // property index of "Fill" (valid if resolved)
    BYTE   kind;        // KIND_FILL / KIND_STROKE
    BYTE   winui;       // 1 if Microsoft.UI.Xaml (WinUI 3) rather than Windows.UI.Xaml
    BYTE   resolved;    // fillIndex/original captured
    BYTE   overridden;  // we currently have our brush set on it
};
static Target g_targets[MAX_TARGETS];

// ---- Per UI thread: sink window + small brush cache ----
#define MAX_THREADS 32
#define MAX_BRUSHES 16

struct BrushEntry {
    UINT64 handle;
    UINT32 argb;
    BYTE   winui;
};
struct ThreadState {
    DWORD      tid;     // 0 = free slot
    HWND       sink;
    BrushEntry brushes[MAX_BRUSHES];
    UINT32     nBrushes;
    UINT32     nextEvict;
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
    return NULL;
}

static Target* FindTarget(UINT64 handle)
{
    if (handle == 0) return NULL;
    for (int i = 0; i < MAX_TARGETS; i++)
        if (g_targets[i].handle == handle) return &g_targets[i];
    return NULL;
}

static bool AddTarget(UINT64 handle, DWORD tid, BYTE kind, BYTE winui)
{
    Target* t = FindTarget(handle);
    if (t) { t->tid = tid; t->kind = kind; t->winui = winui; return true; }
    for (int i = 0; i < MAX_TARGETS; i++) {
        if (g_targets[i].handle == 0) {
            ZeroMemory(&g_targets[i], sizeof(Target));
            g_targets[i].handle = handle;
            g_targets[i].tid = tid;
            g_targets[i].kind = kind;
            g_targets[i].winui = winui;
            return true;
        }
    }
    return false;
}

// Drop every target that is `removed` or lives somewhere underneath it.
static void DropTargetsUnder(UINT64 removed)
{
    for (int i = 0; i < MAX_TARGETS; i++) {
        UINT64 cur = g_targets[i].handle;
        if (cur == 0) continue;
        for (int depth = 0; depth < 256 && cur != 0; depth++) {
            if (cur == removed) { ZeroMemory(&g_targets[i], sizeof(Target)); break; }
            MapSlot* s = MapFind(cur);
            if (!s) break;
            cur = s->parent;
        }
    }
}

// -------------------------------------------------------------------------------------
//  Small helpers (no CRT formatting needed)
// -------------------------------------------------------------------------------------
static void FormatArgb(UINT32 argb, WCHAR out[10])
{
    static const WCHAR hex[] = L"0123456789ABCDEF";
    out[0] = L'#';
    for (int i = 0; i < 8; i++) out[1 + i] = hex[(argb >> (28 - 4 * i)) & 0xF];
    out[9] = 0;
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

static bool StrEq(const WCHAR* a, const WCHAR* b)
{
    return a && b && wcscmp(a, b) == 0;
}

static bool IsRectangleType(const WCHAR* type, BYTE* winui)
{
    if (StrEq(type, L"Windows.UI.Xaml.Shapes.Rectangle"))   { *winui = 0; return true; }
    if (StrEq(type, L"Microsoft.UI.Xaml.Shapes.Rectangle")) { *winui = 1; return true; }
    return false;
}

static void PinModule()
{
    HMODULE h = NULL;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                       (LPCWSTR)(void*)&PinModule, &h);
}

// -------------------------------------------------------------------------------------
//  XAML property helpers (call WITHOUT holding g_lock, on the element's UI thread)
// -------------------------------------------------------------------------------------

// Finds the "Fill" property index and, if the current Fill is a local value that is an
// object handle, that original brush handle so we can put it back exactly later.
static bool ResolveFill(IVisualTreeService* svc, UINT64 element, UINT32* fillIndex, UINT64* original)
{
    unsigned int srcCount = 0, propCount = 0;
    PropertyChainSource* srcs = NULL;
    PropertyChainValue* vals = NULL;

    HRESULT hr = svc->GetPropertyValuesChain(element, &srcCount, &srcs, &propCount, &vals);
    if (FAILED(hr)) return false;

    int first = -1, effective = -1;
    if (vals) {
        for (unsigned int i = 0; i < propCount; i++) {
            if (!StrEq(vals[i].PropertyName, L"Fill")) continue;
            if (first < 0) first = (int)i;
            if (!vals[i].Overridden && effective < 0) effective = (int)i;
        }
    }

    bool ok = false;
    if (first >= 0) {
        *fillIndex = vals[first].Index;
        *original = 0;
        if (effective >= 0) {
            const PropertyChainValue& v = vals[effective];
            bool isLocal = v.PropertyChainIndex < srcCount && srcs &&
                           srcs[v.PropertyChainIndex].Source == BaseValueSourceLocal;
            if (isLocal && (v.MetadataBits & IsValueHandle))
                *original = ParseDecimal(v.Value);
        }
        ok = true;
    }

    // Free everything the service allocated for us (caller owns [out] data).
    if (srcs) {
        for (unsigned int i = 0; i < srcCount; i++) {
            SysFreeString(srcs[i].TargetType);
            SysFreeString(srcs[i].Name);
            SysFreeString(srcs[i].SrcInfo.FileName);
            SysFreeString(srcs[i].SrcInfo.Hash);
        }
        CoTaskMemFree(srcs);
    }
    if (vals) {
        for (unsigned int i = 0; i < propCount; i++) {
            SysFreeString(vals[i].Type);
            SysFreeString(vals[i].DeclaringType);
            SysFreeString(vals[i].ValueType);
            SysFreeString(vals[i].ItemType);
            SysFreeString(vals[i].Value);
            SysFreeString(vals[i].PropertyName);
        }
        CoTaskMemFree(vals);
    }
    return ok;
}

static void EvictBrush(DWORD tid, UINT64 brush)
{
    Locker l;
    ThreadState* ts = FindThread(tid);
    if (!ts) return;
    for (UINT32 i = 0; i < ts->nBrushes; i++)
        if (ts->brushes[i].handle == brush) ts->brushes[i].handle = 0;
}

// Returns a SolidColorBrush handle for this color on the current UI thread (cached).
static UINT64 GetBrush(IVisualTreeService* svc, DWORD tid, UINT32 argb, BYTE winui)
{
    {
        Locker l;
        ThreadState* ts = FindThread(tid);
        if (ts) {
            for (UINT32 i = 0; i < ts->nBrushes; i++) {
                const BrushEntry& b = ts->brushes[i];
                if (b.handle && b.argb == argb && b.winui == winui) return b.handle;
            }
        }
    }

    WCHAR text[10];
    FormatArgb(argb, text);
    BSTR type = SysAllocString(winui ? L"Microsoft.UI.Xaml.Media.SolidColorBrush"
                                     : L"Windows.UI.Xaml.Media.SolidColorBrush");
    BSTR value = SysAllocString(text);
    UINT64 handle = 0;
    if (type && value) {
        if (FAILED(svc->CreateInstance(type, value, &handle))) handle = 0;
    }
    SysFreeString(type);
    SysFreeString(value);
    if (!handle) return 0;

    Locker l;
    ThreadState* ts = FindThread(tid);
    if (ts) {
        UINT32 slot;
        if (ts->nBrushes < MAX_BRUSHES) slot = ts->nBrushes++;
        else slot = (ts->nextEvict++) % MAX_BRUSHES;
        ts->brushes[slot].handle = handle;
        ts->brushes[slot].argb = argb;
        ts->brushes[slot].winui = winui;
    }
    return handle;
}

static void UpdateTarget(UINT64 handle, const Target& src)
{
    Locker l;
    Target* t = FindTarget(handle);
    if (!t) return;
    t->resolved = src.resolved;
    t->fillIndex = src.fillIndex;
    t->original = src.original;
    t->overridden = src.overridden;
}

static bool RestoreOne(IVisualTreeService* svc, const Target& t)
{
    if (!t.resolved) return false;
    if (t.original && SUCCEEDED(svc->SetProperty(t.handle, t.original, t.fillIndex))) return true;
    return SUCCEEDED(svc->ClearProperty(t.handle, t.fillIndex));
}

// Applies g_state to every target owned by the calling UI thread. Returns #elements changed.
static UINT ApplyCurrentState()
{
    const DWORD tid = GetCurrentThreadId();
    IVisualTreeService* svc = NULL;
    OpacState st;
    Target snap[MAX_TARGETS];
    UINT n = 0;
    {
        Locker l;
        svc = g_service;
        st = g_state;
        for (int i = 0; i < MAX_TARGETS; i++)
            if (g_targets[i].handle && g_targets[i].tid == tid) snap[n++] = g_targets[i];
    }
    if (!svc) return 0;

    UINT count = 0;
    for (UINT i = 0; i < n; i++) {
        Target t = snap[i];

        bool paint = false;
        UINT32 color = 0;
        if (st.overrideOn) {
            if (t.kind == KIND_FILL)  { paint = true; color = st.argb; }
            else if (st.hideStroke)   { paint = true; color = 0x00000000; }
        }

        if (paint) {
            if (!t.resolved) {
                UINT32 idx = 0; UINT64 orig = 0;
                if (!ResolveFill(svc, t.handle, &idx, &orig)) continue;
                t.resolved = 1; t.fillIndex = idx; t.original = orig;
                UpdateTarget(t.handle, t);
            }
            UINT64 brush = GetBrush(svc, tid, color, t.winui);
            if (!brush) continue;
            HRESULT hr = svc->SetProperty(t.handle, brush, t.fillIndex);
            if (FAILED(hr)) {
                // Cached brush may have gone stale: retry once with a fresh one.
                EvictBrush(tid, brush);
                brush = GetBrush(svc, tid, color, t.winui);
                if (!brush) continue;
                hr = svc->SetProperty(t.handle, brush, t.fillIndex);
            }
            if (SUCCEEDED(hr)) {
                t.overridden = 1;
                UpdateTarget(t.handle, t);
                count++;
            }
        } else if (t.overridden) {
            if (RestoreOne(svc, t)) {
                t.overridden = 0;
                UpdateTarget(t.handle, t);
                count++;
            }
        }
    }
    return count;
}

// -------------------------------------------------------------------------------------
//  Sink window (one per taskbar UI thread)
// -------------------------------------------------------------------------------------
static void ForgetThread(DWORD tid, HWND hwnd)
{
    Locker l;
    ThreadState* ts = FindThread(tid);
    if (!ts || ts->sink != hwnd) return;
    for (int i = 0; i < MAX_TARGETS; i++)
        if (g_targets[i].tid == tid) ZeroMemory(&g_targets[i], sizeof(Target));
    ZeroMemory(ts, sizeof(ThreadState));
}

static LRESULT CALLBACK SinkWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    switch (msg) {
    case WM_OPAC_SET: {
        {
            Locker l;
            g_state.argb       = (UINT32)(wParam & 0xFFFFFFFFu);
            g_state.overrideOn = (lParam & OPAC_FLAG_ON) != 0;
            g_state.hideStroke = (lParam & OPAC_FLAG_HIDE_STROKE) != 0;
        }
        return (LRESULT)ApplyCurrentState();
    }
    case WM_OPAC_RESTORE: {
        {
            Locker l;
            g_state.overrideOn = false;
        }
        return (LRESULT)ApplyCurrentState();
    }
    case WM_OPAC_PING:
        return OPAC_PING_REPLY;
    case WM_OPAC_PENDING: {
        bool on;
        {
            Locker l;
            on = g_state.overrideOn;
        }
        if (on) ApplyCurrentState();
        return 0;
    }
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
                ts->nBrushes = 0;
                ts->nextEvict = 0;
                ZeroMemory(ts->brushes, sizeof(ts->brushes));
            }
            ts->sink = hwnd;
            stored = true;
        }
    }
    if (!stored) { DestroyWindow(hwnd); return NULL; }
    return hwnd;
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
    HRESULT hr = a->svc->AdviseVisualTreeChange(static_cast<IVisualTreeServiceCallback*>(a->cb));
    if (FAILED(hr)) InterlockedExchange(&g_advised, 0);   // allow a later injection to retry
    a->cb->Release();
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

    // IObjectWithSite
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) override
    {
        IUnknown* old = NULL;
        {
            Locker l;
            old = m_site;
            m_site = site;
            if (site) site->AddRef();
        }
        if (old) old->Release();
        if (!site) return S_OK;

        IVisualTreeService* svc = NULL;
        HRESULT hr = site->QueryInterface(IID_IVisualTreeService, (void**)&svc);
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

        PinModule();   // callbacks + window procs live in this DLL: never unload it

        AdviseArgs* a = (AdviseArgs*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(AdviseArgs));
        if (!a) { InterlockedExchange(&g_advised, 0); return E_OUTOFMEMORY; }
        a->svc = use;
        a->cb = static_cast<IVisualTreeServiceCallback2*>(this);
        AddRef();

        HANDLE th = CreateThread(NULL, 0, AdviseThread, a, 0, NULL);
        if (!th) {
            DWORD err = GetLastError();
            HeapFree(GetProcessHeap(), 0, a);
            Release();
            InterlockedExchange(&g_advised, 0);
            return HRESULT_FROM_WIN32(err ? err : ERROR_GEN_FAILURE);
        }
        CloseHandle(th);
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
        UINT64 handle = element.Handle ? element.Handle : relation.Child;
        if (handle == 0) return S_OK;

        if (mutationType == Add) {
            bool inBg = element.Type && wcsstr(element.Type, L"TaskbarBackground") != NULL;
            int kind = -1;
            BYTE winui = 0;
            const DWORD tid = GetCurrentThreadId();
            {
                Locker l;
                if (!inBg && relation.Parent) {
                    MapSlot* p = MapFind(relation.Parent);
                    if (p && (p->flags & NODE_IN_BG)) inBg = true;
                }
                MapPut(handle, relation.Parent, inBg ? NODE_IN_BG : 0);

                if (inBg && IsRectangleType(element.Type, &winui)) {
                    if (StrEq(element.Name, L"BackgroundFill"))        kind = KIND_FILL;
                    else if (StrEq(element.Name, L"BackgroundStroke")) kind = KIND_STROKE;
                }
                if (kind >= 0 && !AddTarget(handle, tid, (BYTE)kind, winui)) kind = -1;
            }
            if (kind >= 0) {
                // Don't touch XAML from inside the callback: defer to our window proc.
                HWND sink = EnsureSink(tid);
                if (sink) PostMessageW(sink, WM_OPAC_PENDING, 0, 0);
            }
        } else if (mutationType == Remove) {
            Locker l;
            DropTargetsUnder(handle);
            MapErase(handle);
        }
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
    if (!ppv) return E_POINTER;
    *ppv = NULL;
    if (!IsEqualGUID(rclsid, kCLSID_OpacbarTap)) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    // Once subscribed we stay loaded for the life of Explorer (we are also pinned).
    if (g_advised || g_objects > 0 || g_locks > 0) return S_FALSE;
    return S_OK;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
        InitializeCriticalSection(&g_lock);
    }
    return TRUE;
}
