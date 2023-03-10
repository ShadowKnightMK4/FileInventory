using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace OdinSearchEngine
{
    
    /// <summary>
    /// Search the local system for files/folders 
    /// </summary>
    public class OdinSearch
    {
        /// <summary>
        /// reset before we started searching
        /// </summary>
        public void Reset()
        {
            KillSearch();
            ClearSearchAnchorList();
            ClearSearchTargetList();
        }
        

        /// <summary>
        /// pairing a Thread with a a cancilation token.
        /// </summary>
        internal class WorkerThreadWithCancelToken
        {
            public Thread Thread;
            public CancellationTokenSource Token;
        }

        /// <summary>
        /// This class is used for storing both a <see cref="SearchTarget"/> with a predone <see cref="Regex"/> list containing the regex to match file
        /// </summary>
        internal class SearchTargetPreDoneRegEx
        {
            public SearchTargetPreDoneRegEx(SearchTarget SearchTarget)
            {
                PreDoneRegEx = SearchTarget.ConvertFileNameToRegEx();
                this.SearchTarget = SearchTarget;
            }
            public SearchTarget SearchTarget;
            public List<Regex> PreDoneRegEx;
        }
        /// <summary>
        /// This class is passed to the worker thread as an argument
        /// </summary>
        internal class WorkerThreadArgs
        {
            /// <summary>
            /// used in the workerthread code. This contains a single start point gotton from a list generated by <see cref="SearchAnchor.SplitRoots()"/>
            /// </summary>
            public SearchAnchor StartFrom;
            /// <summary>
            /// used in the workerthread code. This contains a list of what to look for
            /// </summary>
            public readonly List<SearchTarget> Targets = new();
            /// <summary>
            /// Used to let outside (of the thread anyway) to be able to tell the worker thread to quit. It's checked every file.
            /// </summary>
            public CancellationToken Token;
            /// <summary>
            /// Used in the worker thread.  This is how it will send messages and results outside of its thread.
            /// </summary>
            public OdinSearch_OutputConsumerBase Coms;
        }
        /// <summary>
        /// for future: 
        /// </summary>
        readonly object TargetLock = new object();

        /// <summary>
        /// Locked when sending a match to the output
        /// </summary>
        readonly object ResultsLock = new object();

        #region Worker Thread stuff
        /// <summary>
        /// During the search, this contains all worker thread copies
        /// </summary>
        List<WorkerThreadWithCancelToken> WorkerThreads = new List<WorkerThreadWithCancelToken>();


        /// <summary>
        /// Get the current number of worker thread
        /// </summary>
        public int WorkerThreadCount { get { return WorkerThreads.Count; } }

        /// <summary>
        /// Call Thread.Join() for all worker threads spawned in the list. Your code will functionally be awaiting until it is done
        /// </summary>
        public void WorkerThreadJoin()
        {
            WorkerThreads.ForEach(p => { p.Thread.Join(); });
        }
        /// <summary>
        /// get if any of the worker threads are alive and running still.
        /// </summary>
        public bool HasActiveSearchThreads
        {
            get
            {
                int running_count = 0;
                for (int step = 0; step < WorkerThreads.Count; step++)
                {
                    if (WorkerThreads[step].Thread.ThreadState == (ThreadState.Running))
                    {
                        running_count++;
                        break;
                    }
                }
                return (running_count > 0);
            }
        }

        #endregion


        #region Code for dealing with setting targets
        /// <summary>
        /// Add what to look for here.
        /// </summary>
        readonly List<SearchTarget> Targets = new List<SearchTarget>();

        public void AddSearchTarget(SearchTarget target)
        {
            Targets.Add(target);
        }

        public void ClearSearchTargetList()
        {
            Targets.Clear();
        }
        public SearchTarget[] GetSearchTargetsAsArray()
        {
            return Targets.ToArray();
        }

        public ReadOnlyCollection<SearchTarget> GetSearchTargetsReadOnly()
        {
            return Targets.AsReadOnly();
        }
        #endregion
        #region Code for dealing with setting anchors
        /// <summary>
        /// Add Where to look here. Note that each anchor gets a worker thread.
        /// </summary>
        readonly List<SearchAnchor> Anchors = new List<SearchAnchor>();

        public void AddSearchAnchor(SearchAnchor Anchor)
        {
            Anchors.Add(Anchor);
        }

        public void ClearSearchAnchorList()
        {
            Anchors.Clear();
        }

        public SearchAnchor[] GetSearchAnchorsAsArray()
        {
            return Anchors.ToArray();
        }

        public ReadOnlyCollection<SearchAnchor> GetSearchAnchorReadOnly()
        {
            return Anchors.AsReadOnly();
        }

        #endregion

        #region Code with Dealing with threads

        /// <summary>
        /// False Means we don't lock a object to aid synching when sending output to a <see cref="OdinSearch_OutputConsumerBase"/> based class.  
        /// </summary>
        public bool ThreadSynchResults
        {
            get
            {
                return ThreadSynchResultsBacking;
            }
            set
            {
                ThreadSynchResultsBacking = value;
            }
        }
        protected bool ThreadSynchResultsBacking = true;

        public void KillSearch()
        {
            if (WorkerThreads.Count != 0)
            {
                foreach (var workerThread in WorkerThreads)
                {
                    workerThread.Token.Cancel();
                    
                }
            }

            WorkerThreads.Clear();
        }

        /// <summary>
        /// Search specs must pass this before search is go. We are looking to just fail impossible combinations
        /// </summary>
        /// <param name="Arg"></param>
        /// <returns></returns>
        /// <remarks>Honstestly just returns true with this current build</remarks>
        bool SanityChecks(WorkerThreadArgs Arg)
        {
            return true;
        }

        /// <summary>
        /// Compares if the specified thing to look for matches this possible file system item
        /// </summary>
        /// <param name="SearchTarget">A class containing both the <see cref="SearchTarget"/> and the predone <see cref="Regex"/> stuff</param>
        /// <param name="Info">look for this</param>
        /// <returns>true if matchs and false if not.</returns>

        bool MatchThis(SearchTargetPreDoneRegEx SearchTarget, FileSystemInfo Info)
        {
            bool FinalMatch= true;
            bool MatchedOne = false;
            bool MatchedFailedOne = false;

            if (SearchTarget == null)
            {
                throw new ArgumentNullException(nameof(SearchTarget));
            }
            if (Info == null)
            {
                throw new ArgumentNullException(nameof(Info));
            }


            // if the filename check has not been disabled
            if (!SearchTarget.SearchTarget.FileNameMatching.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.Skip))
            {
                // special case in <the Convert to RegEx routine.  Empty list means we've matched all
                if (SearchTarget.PreDoneRegEx.Count == 0)
                {
                    MatchedOne = true;
                }
                else
                {
                    foreach (var Regs in SearchTarget.PreDoneRegEx)
                    {
                        if (Regs.IsMatch(Info.Name))
                        {
                            MatchedOne = true;
                        }
                        else
                        {
                            MatchedFailedOne = true;
                        }
                    }
                }
                /*
                 * MatchOne & MatchFailOne true means we're not a match all
                 * 
                 * MatchOne true and MatchFailOne false means at least one matched but not all
                 * 
                 * MatchOne false and MatchFail false means nothing matched
                 * */
                if (SearchTarget.SearchTarget.FileNameMatching.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.MatchAll))
                {
                    if (SearchTarget.SearchTarget.FileNameMatching.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.Invert))
                    {
                        if (MatchedOne)
                        {
                            FinalMatch = false;
                            goto exit;
                        }
                    }
                    else
                    {
                        if (MatchedFailedOne)
                        {
                            FinalMatch = false;
                            goto exit;
                        }
                    }
                }

                if (SearchTarget.SearchTarget.FileNameMatching.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.MatchAny))
                {
                    if (SearchTarget.SearchTarget.FileNameMatching.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.Invert))
                    {
                        if (!MatchedFailedOne)
                        {
                            FinalMatch = false;
                            goto exit;
                        }
                    }
                    else
                    {
                        if (!MatchedOne)
                        {
                            FinalMatch = false;
                            goto exit;
                        }
                    }
                }
            }

            MatchedOne = false;
            MatchedFailedOne = false;
            if (!SearchTarget.SearchTarget.AttribMatching1Style.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.Skip))
            {
                if ((SearchTarget.SearchTarget.AttributeMatching1.HasFlag(FileAttributes.Normal) != true) && 
                   (SearchTarget.SearchTarget.AttributeMatching1 != 0))
                {
                    if (SearchTarget.SearchTarget.AttribMatching1Style.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.MatchAll))
                    {
                        if (SearchTarget.SearchTarget.AttributeMatching1 == Info.Attributes)
                        {
                            if ((SearchTarget.SearchTarget.AttribMatching1Style.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.Invert)))
                            {
                                FinalMatch = false;
                                goto exit;
                            }
                        }
                    }

                    if (SearchTarget.SearchTarget.AttribMatching1Style.HasFlag(OdinSearchEngine.SearchTarget.MatchStyleString.MatchAny))
                    {
                        if ( (SearchTarget.SearchTarget.AttributeMatching1 & Info.Attributes) == 0)
                        {
                            FinalMatch = false;
                            goto exit;
                        }
                    }

                }
            }
            
            MatchedOne = false;
            MatchedFailedOne = false;
            exit:
            return FinalMatch;
        }

        /// <summary>
        /// Compares if the specified thing to look for matches this possible file system item
        /// </summary>
        /// <param name="SearchTarget">A class containing both the <see cref="SearchTarget"/> and the predone <see cref="Regex"/> stuff</param>
        /// <param name="Info">look for this</param>
        /// <returns>true if matchs and false if not.</returns>
        bool MatchThis(SearchTargetPreDoneRegEx SearchTarget, FileInfo Info)
        {
            return MatchThis(SearchTarget, Info as FileSystemInfo);
        }
        /// <summary>
        /// Compares if the specified thing to look for matches this possible file system item
        /// </summary>
        /// <param name="SearchTarget">A class containing both the <see cref="SearchTarget"/> and the predone <see cref="Regex"/> stuff</param>
        /// <param name="Info">look for this</param>
        /// <returns>true if matchs and false if not.</returns>
        bool MatchThis(SearchTargetPreDoneRegEx SearchTarget, DirectoryInfo Info)
        {
            return MatchThis(SearchTarget, Info as FileSystemInfo);
        }
        /// <summary>
        /// Unpack the WorkerThreadArgs and go to work
        /// </summary>
        /// <param name="Args"></param>
        void ThreadWorkerProc(object Args)
        {
            Queue<DirectoryInfo> FolderList= new Queue<DirectoryInfo>();
            List<SearchTargetPreDoneRegEx> TargetWithRegEx = new List<SearchTargetPreDoneRegEx>();
            WorkerThreadArgs TrueArgs = Args as WorkerThreadArgs;

            
            
           if (TrueArgs != null ) 
            { 
                if (TrueArgs.Targets.Count > 0)
                {
                    if (TrueArgs.StartFrom != null)
                    {
                        // prececulate the search target info
                        foreach (SearchTarget Target in TrueArgs.Targets)
                        {
                            TargetWithRegEx.Add(new SearchTargetPreDoneRegEx(Target));
                        }
                        
                        // add root[0] to the queue to pull from
                        FolderList.Enqueue(TrueArgs.StartFrom.roots[0]);

                        // label is used as a starting point to loop back to for looking at subfolders
                    Reset:

                        
                        if (FolderList.Count > 0)
                        {
                            // should an exception happen during getting folder/file names, this is set
                            bool ErrorPrune = false;
                            
                            DirectoryInfo CurrentLoc = FolderList.Dequeue();

                            // files in the CurrentLoc
                            FileInfo[] Files = null;
                            // folders in the CurrentLc
                            DirectoryInfo[] Folders = null;
                            try
                            {
                                Files = CurrentLoc.GetFiles();
                                Folders = CurrentLoc.GetDirectories();
                            }
                            catch (IOException e)
                            {
                                TrueArgs.Coms.Messaging("Unable to get file or listing for folder at " + CurrentLoc.FullName + " Reason: " + e.Message);
                                TrueArgs.Coms.Blocked(CurrentLoc.ToString());
                                ErrorPrune = true;
                            }
                            catch (UnauthorizedAccessException e)
                            {
                                TrueArgs.Coms.Messaging("Unable to get file or listing for folder at " + CurrentLoc.FullName + " Reason Access Denied");
                                TrueArgs.Coms.Blocked(CurrentLoc.ToString());
                                ErrorPrune = true;
                            }



                            if (!ErrorPrune)

                            foreach (SearchTargetPreDoneRegEx Target in TargetWithRegEx)
                            {
                                bool Pruned = false;
                                
                                // skip this compare if we're  looking for a directory

                                if (Target.SearchTarget.AttributeMatching1 != 0)
                                {
                                    if (Target.SearchTarget.AttributeMatching1.HasFlag(FileAttributes.Directory))
                                    {
                                        Pruned = true;
                                    }
                                }


                                if (!Pruned)
                                {
                                    // file check
                                    

                                    foreach (FileInfo Possible in Files)
                                    {
                                        bool isMatched = MatchThis(Target, Possible);
                                        if (isMatched)
                                        {
                                            if (!ThreadSynchResults)
                                            {
                                                TrueArgs.Coms.Match(Possible);
                                            }
                                            else
                                            {
                                                lock (ResultsLock)
                                                {
                                                    TrueArgs.Coms.Match(Possible);
                                                }
                                            }
                                        }
                                    }
                                }
                                Pruned = false;

                                if (!Pruned)
                                {
                                    // folder check
                                    foreach (DirectoryInfo Possible in Folders)
                                    {
                                        bool isMatched = MatchThis(Target, Possible);
                                        if (isMatched)
                                        {
                                            if (!ThreadSynchResults)
                                            {
                                                TrueArgs.Coms.Match(Possible);
                                            }
                                            else
                                            {
                                                lock (ResultsLock)
                                                {
                                                    TrueArgs.Coms.Match(Possible);
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            if (TrueArgs.StartFrom.EnumSubFolders)
                            {
                                if (!ErrorPrune)
                                foreach (DirectoryInfo Folder in Folders)
                                {
                                    FolderList.Enqueue(Folder);
                                }
                            }
                        }

                        if (FolderList.Count > 0)
                        {
                            goto Reset;
                        }
                    }
                }
            }
        }
        #endregion
        /// <summary>
        /// Start the search rolling. 
        /// </summary>
        /// <param name="Coms">This class is how the search communicates with your code. Cannot be null</param>
        /// <exception cref="IOException">Is thrown if Search is called while searching. </exception>
        /// <exception cref="ArgumentNullException">Is thrown if the Coms argument is null</exception>
        public void Search(OdinSearch_OutputConsumerBase Coms)
        {
            if (Coms == null)
            {
                throw new ArgumentNullException(nameof(Coms));
            }
            else
            {
                if (WorkerThreads.Count != 0)
                {
                    throw new InvalidOperationException("Search in Progress. Workerthread != 0");
                }
                else
                {

                    foreach (SearchAnchor Anchor in Anchors)
                    {
                        var AnchorList = Anchor.SplitRoots();
                        foreach (SearchAnchor SmallAnchor in AnchorList)
                        {
                            WorkerThreadArgs Args = new();
                            Args.StartFrom = SmallAnchor;
                            Args.Targets.AddRange(Targets);
                            Args.Coms = Coms;

                            WorkerThreadWithCancelToken Worker = new WorkerThreadWithCancelToken();
                            Worker.Thread = new Thread(() => ThreadWorkerProc(Args));
                            Worker.Token = new CancellationTokenSource();

                            Args.Token = Worker.Token.Token;


                            WorkerThreads.Add(Worker);
                        }
                    }
                    /*

                        foreach (SearchAnchor Anchor in Anchors)
                    {
                        WorkerThreadArgs Args = new();
                        Args.StartFrom = Anchor;
                        
                        Args.Targets.AddRange(Targets);
                        Args.Coms = Coms;

                        
                        WorkerThreadWithCancelToken Worker = new WorkerThreadWithCancelToken();
                        Worker.Thread = new Thread(() => ThreadWorkerProc(Args)); 
                        Worker.Token = new CancellationTokenSource();

                        Args.Token = Worker.Token.Token;
                        

                        WorkerThreads.Add(Worker);
                    }
                    */
                    foreach (WorkerThreadWithCancelToken t in WorkerThreads)
                    {
                        t.Thread.Start();
                    }
                }
            }
        }
    }
}
