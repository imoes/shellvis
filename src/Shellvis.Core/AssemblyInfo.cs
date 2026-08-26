using System.Runtime.CompilerServices;

// The verification harness reaches internal types.
//
// Preferred over the two alternatives. Making a type public to test it changes the thing
// being tested and puts it in the API surface for ever; reaching it by reflection cannot
// work at all for a method taking a ReadOnlySpan, because a ref struct cannot be boxed into
// the object[] that Invoke wants.
//
// BlockingAudioStream is the case that forced the decision: it is the bridge between audio
// capture and the recogniser, it is where dictation was losing its audio, and testing it
// means pushing spans into it the way a capture callback does.
[assembly: InternalsVisibleTo("Shellvis.DesktopProbe")]
