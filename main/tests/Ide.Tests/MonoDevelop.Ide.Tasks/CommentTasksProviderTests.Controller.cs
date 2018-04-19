//
// CommentTasksProviderTests.Controller.cs
//
// Author:
//       Marius Ungureanu <maungu@microsoft.com>
//
// Copyright (c) 2018 
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoDevelop.Core;
using MonoDevelop.Projects;
using NUnit.Framework;
using UnitTests;

namespace MonoDevelop.Ide.Tasks
{
	partial class CommentTasksProviderTests
	{
		class Controller
		{
			static void BindTimeout<T> (TaskCompletionSource<T> tcs)
			{
				// Really conservative timeout.
				const int timeout = 3 * 60 * 1000;

				var ct = new CancellationTokenSource (timeout);
				ct.Token.Register (() => tcs.TrySetCanceled (), useSynchronizationContext: false);
			}

			public class Options
			{
				readonly bool withToDos;
				public string [] ExpectedFiles;
				public (string, string, int, int) [] ExpectedComments;
				public Func<int, int> GetChangeCount;
				public int NotificationCount;

				public Options (bool withToDos)
				{
					this.withToDos = withToDos;

					ExpectedFiles = DefaultGetExpectedFiles ().ToArray ();
					ExpectedComments = DefaultGetExpectedComments ().ToArray ();
					GetChangeCount = cnt => 1;
					NotificationCount = -1;
				}

				IEnumerable<string> DefaultGetExpectedFiles ()
				{
					yield return "AssemblyInfo.cs";
					yield return "Program.cs";
					if (withToDos)
						yield return FileName;
				}

				IEnumerable<(string, string, int, int)> DefaultGetExpectedComments ()
				{
					if (!withToDos)
						yield break;

					yield return ("TODO: Fill this file", "TODO", 0, 3);
					yield return ("FIXME: This is broken", "FIXME", 1, 3);
					yield return ("HACK: Just for the test", "HACK", 2, 3);
					yield return ("UNDONE: Not done yet", "UNDONE", 3, 3);
				}
			}

			bool hasToDos;
			string solFile = Util.GetSampleProject ("console-project", "ConsoleProject.sln");

			public async Task SetupProject (bool withToDos)
			{
				if (withToDos) {
					using (var sol = (Solution)await MonoDevelop.Projects.Services.ProjectService.ReadWorkspaceItem (Util.GetMonitor (), solFile)) {
						var project = sol.GetAllProjects ().Single ();
						await AddToDoFile (project);
					}
				}
			}

			static string WriteFileText (Project project, string file, string content)
			{
				var path = System.IO.Path.Combine (project.BaseDirectory, file);
				System.IO.File.WriteAllText (path, content);
				return path;
			}

			public const string FileName = "TODOs.cs";
			const string content = @"// TODO: Fill this file
// FIXME: This is broken
// HACK: Just for the test
// UNDONE: Not done yet
// CUSTOMTAG: Shouldn't be in first";
			
			async Task AddToDoFile (Project project)
			{
				Assert.IsFalse (hasToDos);

				var path = WriteFileText (project, FileName, content);

				hasToDos = true;
				project.AddFile (new ProjectFile (path, BuildAction.Compile));
				await project.SaveAsync (new ProgressMonitor ());
			}

			public async Task AddToDoFile (Options options = null)
			{
				var project = IdeApp.Workspace.GetAllProjects ().Single ();

				var tcs = RegisterCallback (options);
				BindTimeout (tcs);

				await AddToDoFile (project);
				await tcs.Task;
			}

			public async Task LoadProject (Options options = null)
			{
				var tcs = RegisterCallback (options);

				BindTimeout (tcs);
				// Load the solution into the workspace.
				bool opened = await IdeApp.Workspace.OpenWorkspaceItem (solFile);
				Assert.IsTrue (opened, $"Solution file {solFile} could not be opened");

				await tcs.Task;
			}

			public async Task SetCommentTags (List<CommentTag> tags, Options options = null)
			{
				var tcs = RegisterCallback (options);
				BindTimeout (tcs);

				CommentTag.SpecialCommentTags = tags;
				await tcs.Task;
			}

			public async Task ModifyToDoFile (string toAppend, Options options = null)
			{
				var tcs = RegisterCallback (options);
				BindTimeout (tcs);

				var proj = IdeApp.Workspace.GetAllProjects ().Single ();
				WriteFileText (proj, FileName, content + Environment.NewLine + toAppend);

				await tcs.Task;
			}

			TaskCompletionSource<bool> RegisterCallback (Options options)
			{
				options = options ?? new Options (hasToDos);

				var tcs = new TaskCompletionSource<bool> ();
				var gatheredFiles = new HashSet<string> ();
				int count = 0;
				TaskService.CommentTasksChanged += (s, args) => {
					if (tcs.Task.IsCompleted)
						return;

					++count;

					try {
						Assert.Less (options.NotificationCount + 1, count);
						Assert.AreEqual (options.GetChangeCount (count), args.Changes.Count);

						// Verify changes on each event handler invocation. We cannot guarantee order in which files are parsed.
						foreach (var change in args.Changes) {
							bool wasAdded = gatheredFiles.Add (System.IO.Path.GetFileName (change.FileName));
							Assert.IsTrue (wasAdded, "Added duplicate entry for file");

							Assert.IsNotNull (change.Project);
							Assert.IsNotNull (change.FileName);
						}

						// Validate data at last notification.
						if (count == options.NotificationCount) {
							// See that every file is parsed.
							foreach (var file in options.ExpectedFiles)
								Assert.That (gatheredFiles, Contains.Item (file));

							// See that we got the right todo comments
							var comments = args.Changes [0].TagComments;

							if (options.ExpectedComments.Length == 0) {
								Assert.IsNull (comments);
							} else {
								for (int i = 0; i < options.ExpectedComments.Length; ++i) {
									var (text, key, line, col) = options.ExpectedComments [i];
									var region = new Editor.DocumentRegion (line, col, line, col);

									Assert.AreEqual (text, comments [i].Text);
									Assert.AreEqual (key, comments [i].Key);
									Assert.AreEqual (region, comments [i].Region);
								}
							}
						}
					} catch (Exception ex) {
						tcs.TrySetException (ex);
					}
				};

				return tcs;
			}

			public async Task DisposeAsync ()
			{
				await IdeApp.Workspace.Close ();
			}
		}
	}
}
