using System;
using System.Collections.Generic;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using System.Collections.Specialized;
using System.Diagnostics;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using Sdl.TranslationStudioAutomation.IntegrationApi.Extensions;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Linq;
using Sdl.FileTypeSupport.Framework.BilingualApi;

namespace FiskmoTranslationProvider
{
    public class FiskmoProviderLanguageDirection : ITranslationProviderLanguageDirection
    {
        #region "PrivateMembers"
        private FiskmoProvider _provider;
        private LanguagePair _languageDirection;
        private FiskmoOptions _options;
        private FiskmoProviderElementVisitor _visitor;
        private static Dictionary<string,ConcurrentBag<Document>> processedDocuments = new Dictionary<string, ConcurrentBag<Document>>();
        internal static Dictionary<string,MarianProcess> _marianProcesses = new Dictionary<string, MarianProcess>();
        private string langpair;
        #endregion

        #region "ITranslationProviderLanguageDirection Members"



        /// <summary>
        /// Instantiates the variables and fills the list file content into
        /// a Dictionary collection object.
        /// </summary>
        /// <param name="provider"></param>
        /// <param name="languages"></param>
        #region "ListTranslationProviderLanguageDirection"
        public FiskmoProviderLanguageDirection(FiskmoProvider provider, LanguagePair languages)
        {
            #region "Instantiate"
            // UT.LogMessageToFile("Init ListTranslationProviderLanguageDirection");
            
            _provider = provider;
            _languageDirection = languages;
            _options = _provider.Options;
            
            

            if (Boolean.Parse(_options.pregenerateMt))
            {
                EditorController editorController = SdlTradosStudio.Application.GetController<EditorController>();
                editorController.ActiveDocumentChanged += DocChanged;
            }

            _visitor = new FiskmoProviderElementVisitor(_options);

            var sourceCode = this._languageDirection.SourceCulture.TwoLetterISOLanguageName;
            var targetCode = this._languageDirection.TargetCulture.TwoLetterISOLanguageName;
            this.langpair = $"{sourceCode}-{targetCode}";

            if (!FiskmoProviderLanguageDirection.processedDocuments.ContainsKey(this.langpair))
            {
                FiskmoProviderLanguageDirection.processedDocuments[this.langpair] = new ConcurrentBag<Document>();
            }

            var modelManager = new ModelManager();

            //Start a marian instance if one has not been started or the previous one has exited
            //for some reason.
            if (!FiskmoProviderLanguageDirection._marianProcesses.ContainsKey(this.langpair)
                || FiskmoProviderLanguageDirection._marianProcesses[this.langpair].MtPipe.HasExited)
            {
                //if ((sourceCode == "sv" && targetCode == "fi") || (sourceCode == "fi" && targetCode == "sv"))
                if (true)
                {
                    var latestModelDir = modelManager.GetLatestModelDir(sourceCode, targetCode);

                    FiskmoProviderLanguageDirection._marianProcesses[this.langpair] =
                        new MarianProcess(latestModelDir, sourceCode, targetCode);
                }
            }
            #endregion
        }

        //Whenever doc changes, start translating the segments and caching translations
        private void DocChanged(object sender, DocumentEventArgs e)
        {
            if (e.Document == null)
            {
                return;
            }

            var project = e.Document.Project;
            var projectInfo = project.GetProjectInfo();

            //Don't generate for incorrect language pairs
            if (projectInfo.SourceLanguage.CultureInfo.TwoLetterISOLanguageName != this.SourceLanguage.TwoLetterISOLanguageName ||
                !projectInfo.TargetLanguages.Select(x => x.CultureInfo.TwoLetterISOLanguageName).Contains(this.TargetLanguage.TwoLetterISOLanguageName))
            {
                return;
            }

            var projectTpConfig = project.GetTranslationProviderConfiguration();
            var tpEntries = projectTpConfig.Entries;
            var activeFiskmoTp = tpEntries.SingleOrDefault(
                x =>
                    x.MainTranslationProvider.Enabled &&
                    x.MainTranslationProvider.Uri.OriginalString.Contains("fiskmoprovider")
                );

            if (activeFiskmoTp != null &&
                activeFiskmoTp.MainTranslationProvider.Uri.OriginalString.Contains("pregenerateMt=True") &&
                !FiskmoProviderLanguageDirection.processedDocuments[this.langpair].Contains(e.Document))
            {
                processedDocuments[this.langpair].Add(e.Document);
                Task.Run(() => TranslateDocumentSegments(e.Document));
            }
        }

        //This function starts translating all segments in the document once the document is opened,
        //so that the translator won't have to wait for the translation to finish when opening a segment.
        //Note that Studio contains a feature called LookAhead which attempts to do a similar thing, but
        //this feature appears to be buggy with TMs etc., so it's better to rely on a custom caching system.
        private void TranslateDocumentSegments(Document doc)
        {
            EditorController editorController = SdlTradosStudio.Application.GetController<EditorController>();
            foreach (var segmentPair in doc.SegmentPairs)
            {
                if (segmentPair.Properties.ConfirmationLevel == Sdl.Core.Globalization.ConfirmationLevel.Unspecified)
                {
                    var allTextItems = segmentPair.Source.AllSubItems.Where(x => x is IText);
                    var sourceText = String.Join(" ", allTextItems);

                    var sourceCode = this._languageDirection.SourceCulture.TwoLetterISOLanguageName;
                    var targetCode = this._languageDirection.TargetCulture.TwoLetterISOLanguageName;
                    var langpair = $"{sourceCode}-{targetCode}";

                    //This will generate the translation and cache it for later use
                    FiskmoProviderLanguageDirection._marianProcesses[langpair].Translate(sourceText);
                }
            }
        }

        #endregion

        public System.Globalization.CultureInfo SourceLanguage
        {
            get { return _languageDirection.SourceCulture; }
        }

        public System.Globalization.CultureInfo TargetLanguage
        {
            get { return _languageDirection.TargetCulture; }
        }

        public ITranslationProvider TranslationProvider
        {
            get { return _provider; }
        }


        /// <summary>
        /// Performs the actual search
        /// </summary>
        /// <param name="settings"></param>
        /// <param name="segment"></param>
        /// <returns></returns>
        #region "SearchSegment"
        public SearchResults SearchSegment(SearchSettings settings, Segment segment)
        {
            // Loop through segment elements to 'filter out' e.g. tags in order to 
            // make certain that only plain text information is retrieved for
            // this simplified implementation.

            // TODO: Make it work with tags. Probably alignment information 
            // is needed from the server for this
            #region "SegmentElements"
            _visitor.Reset();
            foreach (var element in segment.Elements)
            {
                element.AcceptSegmentElementVisitor(_visitor);
            }
            #endregion

            #region "SearchResultsObject"
            SearchResults results = new SearchResults();
            results.SourceSegment = segment.Duplicate();

            #endregion
            string sourceText = _visitor.PlainText;

            // If replacements need to be done to the source segment, here iss the place
            // sourceText = sourceText.Replace("foo", "bar");

            //Get the translation from the server
            //string translatedSentence = searchInServer(sourceText);

            var sourceCode = this._languageDirection.SourceCulture.TwoLetterISOLanguageName;
            var targetCode = this._languageDirection.TargetCulture.TwoLetterISOLanguageName;
            var langpair = $"{sourceCode}-{targetCode}";

            string translatedSentence = FiskmoProviderLanguageDirection._marianProcesses[langpair].Translate(sourceText);
            
            if (String.IsNullOrEmpty(translatedSentence))
                return results;
            
            // Look up the currently selected segment in the collection (normal segment lookup).
            if (settings.Mode == SearchMode.FullSearch)
            {
                Segment translation = new Segment(_languageDirection.TargetCulture);
                translation.Add(translatedSentence);
                
                results.Add(CreateSearchResult(segment, translation, _visitor.PlainText, segment.HasTags));
            }
            #region "SegmentLookup"
            if (settings.Mode == SearchMode.NormalSearch)
            {
                Segment translation = new Segment(_languageDirection.TargetCulture);
                translation.Add(translatedSentence);

                results.Add(CreateSearchResult(segment, translation, _visitor.PlainText, segment.HasTags));
            }
            #endregion

            #region "Close"
            return results;
            #endregion
        }
        #endregion

        

        /// <summary>
        /// Creates the translation unit as it is later shown in the Translation Results
        /// window of SDL Trados Studio.
        /// </summary>
        /// <param name="searchSegment"></param>
        /// <param name="translation"></param>
        /// <param name="sourceSegment"></param>
        /// <param name="formattingPenalty"></param>
        /// <returns></returns>
        #region "CreateSearchResult"
        private SearchResult CreateSearchResult(Segment searchSegment, Segment translation,
            string sourceSegment, bool formattingPenalty)
        {

            #region "TranslationUnit"
            TranslationUnit tu = new TranslationUnit();
            Segment orgSegment = new Segment();
            orgSegment.Add(sourceSegment);
            tu.SourceSegment = orgSegment;
            tu.TargetSegment = translation;
            #endregion

            tu.ResourceId = new PersistentObjectToken(tu.GetHashCode(), Guid.Empty);

            #region "TuProperties"
            tu.Origin = TranslationUnitOrigin.MachineTranslation;
            
            SearchResult searchResult = new SearchResult(tu);
            searchResult.ScoringResult = new ScoringResult();
            //searchResult.ScoringResult.BaseScore = score;

            //if (formattingPenalty)
            //{
            //    #region "Draft"
            //    tu.ConfirmationLevel = ConfirmationLevel.Draft;
            //    #endregion

            //    #region "FormattingPenalty"
            //    Penalty penalty = new Penalty(PenaltyType.TagMismatch, 1);
            //    searchResult.ScoringResult.ApplyPenalty(penalty);
            //    #endregion
            //}
            //else
            //{
            //    tu.ConfirmationLevel = ConfirmationLevel.Translated;
            //}
            #endregion

            return searchResult;
        }
        #endregion


        public bool CanReverseLanguageDirection
        {
            get { return false; }
        }

        public SearchResults[] SearchSegments(SearchSettings settings, Segment[] segments)
        {
            SearchResults[] results = new SearchResults[segments.Length];
            for (int p = 0; p < segments.Length; ++p)
            {
                results[p] = SearchSegment(settings, segments[p]);
            }
            return results;
        }

        public SearchResults[] SearchSegmentsMasked(SearchSettings settings, Segment[] segments, bool[] mask)
        {
            if (segments == null)
            {
                throw new ArgumentNullException("segments in SearchSegmentsMasked");
            }
            if (mask == null || mask.Length != segments.Length)
            {
                throw new ArgumentException("mask in SearchSegmentsMasked");
            }

            SearchResults[] results = new SearchResults[segments.Length];
            for (int p = 0; p < segments.Length; ++p)
            {
                if (mask[p])
                {
                    results[p] = SearchSegment(settings, segments[p]);
                }
                else
                {
                    results[p] = null;
                }
            }

            return results;
        }

        public SearchResults SearchText(SearchSettings settings, string segment)
        {
            Segment s = new Segment(_languageDirection.SourceCulture);
            s.Add(segment);
            return SearchSegment(settings, s);
        }

        public SearchResults SearchTranslationUnit(SearchSettings settings, TranslationUnit translationUnit)
        {
            return SearchSegment(settings, translationUnit.SourceSegment);
        }

        public SearchResults[] SearchTranslationUnits(SearchSettings settings, TranslationUnit[] translationUnits)
        {
            SearchResults[] results = new SearchResults[translationUnits.Length];
            for (int p = 0; p < translationUnits.Length; ++p)
            {
                results[p] = SearchSegment(settings, translationUnits[p].SourceSegment);
            }
            return results;
        }

        public SearchResults[] SearchTranslationUnitsMasked(SearchSettings settings, TranslationUnit[] translationUnits, bool[] mask)
        {
            List<SearchResults> results = new List<SearchResults>();

            int i = 0;
            foreach (var tu in translationUnits)
            {
                if (mask == null || mask[i])
                {
                    var result = SearchTranslationUnit(settings, tu);
                    results.Add(result);
                }
                else
                {
                    results.Add(null);
                }
                i++;
            }

            return results.ToArray();
        }



        #region "NotForThisImplementation"
        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnits"></param>
        /// <param name="settings"></param>
        /// <param name="mask"></param>
        /// <returns></returns>
        public ImportResult[] AddTranslationUnitsMasked(TranslationUnit[] translationUnits, ImportSettings settings, bool[] mask)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnit"></param>
        /// <returns></returns>
        public ImportResult UpdateTranslationUnit(TranslationUnit translationUnit)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnits"></param>
        /// <returns></returns>
        public ImportResult[] UpdateTranslationUnits(TranslationUnit[] translationUnits)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnits"></param>
        /// <param name="previousTranslationHashes"></param>
        /// <param name="settings"></param>
        /// <param name="mask"></param>
        /// <returns></returns>
        public ImportResult[] AddOrUpdateTranslationUnitsMasked(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings, bool[] mask)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnit"></param>
        /// <param name="settings"></param>
        /// <returns></returns>
        public ImportResult AddTranslationUnit(TranslationUnit translationUnit, ImportSettings settings)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnits"></param>
        /// <param name="settings"></param>
        /// <returns></returns>
        public ImportResult[] AddTranslationUnits(TranslationUnit[] translationUnits, ImportSettings settings)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not required for this implementation.
        /// </summary>
        /// <param name="translationUnits"></param>
        /// <param name="previousTranslationHashes"></param>
        /// <param name="settings"></param>
        /// <returns></returns>
        public ImportResult[] AddOrUpdateTranslationUnits(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings)
        {
            throw new NotImplementedException();
        }
        #endregion

        #endregion
    }
}
