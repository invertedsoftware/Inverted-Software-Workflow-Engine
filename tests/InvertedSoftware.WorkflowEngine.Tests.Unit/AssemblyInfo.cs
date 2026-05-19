// Copyright (c) Inverted Software. All rights reserved.

// Tests share a process-wide static (FrameworkManager._host). Run them sequentially
// to avoid one test's host overwriting another's while it's mid-publish.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
