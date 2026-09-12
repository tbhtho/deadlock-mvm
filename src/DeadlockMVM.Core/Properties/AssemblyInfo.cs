using System.Runtime.CompilerServices;

// Native internals are exercised by the unit tests and by the dev-only live
// harness (LiveVerify); they stay internal to the shipping app surface.
[assembly: InternalsVisibleTo("DeadlockMVM.Core.Tests")]
[assembly: InternalsVisibleTo("LiveVerify")]

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ReshadeVerify")]
