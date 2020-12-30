﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Metadata;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using VndbSharp;
using VndbSharp.Models;
using VndbSharp.Models.Release;
using VndbSharp.Models.VisualNovel;

namespace PlayniteVndbExtension
{
    public class VndbMetadataProvider : OnDemandMetadataProvider
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        
        private readonly DescriptionFormatter _descriptionFormatter;

        private readonly MetadataRequestOptions _options;
        private readonly IPlayniteAPI _playniteApi;
        private readonly VndbMetadataSettings _settings;
        private readonly List<TagName> _tagDetails;
        private readonly Vndb _vndbClient;
        private readonly string _pluginUserDataPath;

        private List<MetadataField> _availableFields;

        private VisualNovel _vnData;
        private List<ProducerRelease> _vnProducers;

        public VndbMetadataProvider(MetadataRequestOptions options, List<TagName> tagDetails,
            DescriptionFormatter descriptionFormatter, VndbMetadataPlugin plugin)
        {
            _options = options;
            _tagDetails = tagDetails;
            _playniteApi = plugin.PlayniteApi;
            _vndbClient = plugin.VndbClient;
            _pluginUserDataPath = plugin.GetPluginUserDataPath(); 
            _settings = plugin.LoadPluginSettings<VndbMetadataSettings>();
            VndbMetadataSettings.MigrateSettingsVersion(_settings, plugin);
            _descriptionFormatter = descriptionFormatter;
        }

        public override List<MetadataField> AvailableFields
        {
            get
            {
                if (_availableFields == null) _availableFields = GetAvailableFields();

                return _availableFields;
            }
        }

        private List<MetadataField> GetAvailableFields()
        {
            if (_vnData == null)
                if (!GetVndbMetadata())
                    return new List<MetadataField>();

            var fields = new List<MetadataField> {MetadataField.Name};

            if (!string.IsNullOrEmpty(_vnData.Description)) fields.Add(MetadataField.Description);

            if (_vnData.Image != null && IsImageAllowed(_vnData))
                fields.Add(MetadataField.CoverImage);

            if (_vnData.Screenshots.HasItems() &&
                _vnData.Screenshots.Any(IsImageAllowed))
                fields.Add(MetadataField.BackgroundImage);

            if (_vnData.Released != null && _vnData.Released.Year != null 
                                         && ((_vnData.Released.Month != null && _vnData.Released.Day != null) 
                                             || _settings.AllowIncompleteDates) )
                fields.Add(MetadataField.ReleaseDate);

            fields.Add(MetadataField.Genres);
            fields.Add(MetadataField.Links);

            if (HasViableTags()) fields.Add(MetadataField.Tags);

            if (_vnData.Rating != 0) fields.Add(MetadataField.CommunityScore);

            if (_vnProducers != null && _vnProducers.Count(p => p.IsDeveloper) > 0)
                fields.Add(MetadataField.Developers);

            if (_vnProducers != null && _vnProducers.Count(p => p.IsPublisher) > 0)
                fields.Add(MetadataField.Publishers);

            return fields;
        }

        private bool IsImageAllowed(VisualNovel vn)
        {
            return !(vn.ImageRating.SexualAvg >= (int)_settings.ImageMaxSexualityLevel + 0.5 || 
                     vn.ImageRating.ViolenceAvg >= (int)_settings.ImageMaxViolenceLevel + 0.5);
        }
        
        private bool IsImageAllowed(ScreenshotMetadata screenshot)
        {
            return !(screenshot.ImageRating.SexualAvg >= (int)_settings.ImageMaxSexualityLevel + 0.5 ||
                     screenshot.ImageRating.ViolenceAvg >= (int)_settings.ImageMaxViolenceLevel + 0.5);
        }

        private bool HasViableTags()
        {
            return _vnData.Tags.HasItems() && _vnData.Tags.Any(tag =>
            {
                if (TagIsAvailableForScoreAndSpoiler(tag))
                    return _tagDetails.Any(tagDetails =>
                    {
                        if (tag.Id.Equals(tagDetails.Id)) return TagIsInEnabledCategory(tagDetails);

                        return false;
                    });

                return false;
            });
        }

        private bool TagIsAvailableForScoreAndSpoiler(TagMetadata tag)
        {
            return Math.Round(tag.Score, 1) >= _settings.TagMinScore && LowerSpoilerLevel(tag);
        }

        private bool GetVndbMetadata()
        {
            if (_vnData != null) return true;
            
            if (_options.IsBackgroundDownload) return false;
            ReadOnlyCollection<VisualNovel> results = new ReadOnlyCollection<VisualNovel>(new List<VisualNovel>());
            VndbItemOption item = (VndbItemOption) _playniteApi.Dialogs.ChooseItemWithSearch(null, searchString =>
            {
                ReadOnlyCollection<VisualNovel> search;
                if (isVndbId(searchString))
                {
                    search = _vndbClient.GetVisualNovelAsync(VndbFilters.Id.Equals(retrieveVndbId(searchString)),
                            VndbFlags.FullVisualNovel).Result
                        .Items;
                }
                else
                {
                    search = _vndbClient
                        .GetVisualNovelAsync(VndbFilters.Search.Fuzzy(searchString), VndbFlags.FullVisualNovel).Result
                        .Items;
                }

                results = search;
                return new List<GenericItemOption>(search.Select(vn =>
                    {
                        return new VndbItemOption(vn.Name, _descriptionFormatter.RemoveTags(vn.Description), vn.Id);
                    })
                    .ToList());
            }, _options.GameData.Name);

            if (item != null && results.Any(vn => vn.Id.Equals(item.Id)))
            {
                _vnData = results.First(vn => vn.Id.Equals(item.Id));
                _vnProducers = _vndbClient
                    .GetReleaseAsync(VndbFilters.VisualNovel.Equals(_vnData.Id), VndbFlags.Producers)
                    .Result
                    .Items
                    .SelectMany(producers => producers.Producers)
                    .ToList();
                return true;
            }

            _vnData = null;
            return false;
        }

        private bool isVndbId(string searchString)
        {
            var onlyNumbersMatcher = new Regex("^[\\d]+$");
            return searchString.ToLower().StartsWith("id:v") && onlyNumbersMatcher.IsMatch(searchString.Substring(4));
        }

        private uint retrieveVndbId(string searchString)
        {
            return uint.Parse(searchString.Substring(4));
        }

        public override string GetName()
        {
            if (AvailableFields.Contains(MetadataField.Name) && _vnData != null) 
                return _vnData.Name;

            return base.GetName();
        }

        public override List<string> GetGenres()
        {
            if (AvailableFields.Contains(MetadataField.Genres) && _vnData != null)
            {
                var genres = new List<string>();
                genres.Add("Visual Novel");
                return genres;
            }
            
            return base.GetGenres();
        }

        public override DateTime? GetReleaseDate()
        {
            if (AvailableFields.Contains(MetadataField.ReleaseDate) && _vnData != null)
                if (_vnData.Released.Day != null && _vnData.Released.Month != null && _vnData.Released.Year != null)
                {
                    return new DateTime
                    (
                        (int) _vnData.Released.Year.Value,
                        _vnData.Released.Month.Value,
                        _vnData.Released.Day.Value
                    );
                }
                else if (_vnData.Released.Year != null && _settings.AllowIncompleteDates)
                {
                    var day = _vnData.Released.Day ?? 1;
                    var month = _vnData.Released.Month ?? 1;
                    return new DateTime((int) _vnData.Released.Year.Value, month, day);
                }
            
            return base.GetReleaseDate();
        }


        public override List<string> GetDevelopers()
        {
            if (AvailableFields.Contains(MetadataField.Developers) && _vnData != null)
                return new ComparableList<string>(_vnProducers.Where(p => p.IsDeveloper).Select(p => p.Name)
                    .Distinct());

            return base.GetDevelopers();
        }

        public override List<string> GetPublishers()
        {
            if (AvailableFields.Contains(MetadataField.Developers) && _vnData != null)
                return new ComparableList<string>(_vnProducers.Where(p => p.IsPublisher).Select(p => p.Name)
                    .Distinct());

            return base.GetDevelopers();
        }

        public override List<string> GetTags()
        {
            if (AvailableFields.Contains(MetadataField.Tags) && _vnData != null)
            {
                var tags = new List<string>();
                var contentTags = 0;
                var sexualTags = 0;
                var technicalTags = 0;
                foreach (var (tagMetadata, tagName) in _vnData.Tags.OrderByDescending(tag => tag.Score).Select(MapTagToNamedTuple))
                {
                    if (tagName == null)
                    {
                        Logger.Warn("VndbMetadataProvider: Could not find tag: " + tagMetadata.Id);
                    } else if (TagIsAvailableForScoreAndSpoiler(tagMetadata) && 
                               TagIsInEnabledCategory(tagName) && 
                               tags.Count < _settings.MaxAllTags) 
                    {
                        if (tagName.Cat.Equals("cont"))
                        {
                            contentTags = AddContentTagIfNotMax(contentTags, tags, tagName);
                        } else if (tagName.Cat.Equals("ero"))
                        {
                            sexualTags = AddSexualTagIfNotMax(sexualTags, tags, tagName);
                        } else if (tagName.Cat.Equals("tech"))
                        {
                            technicalTags = AddTechnicalTagIfNotMax(technicalTags, tags, tagName);
                        }
                    }
                }
                if (tags.HasNonEmptyItems())
                {
                    return tags;
                }
            }

            return base.GetTags();
        }

        private int AddTechnicalTagIfNotMax(int technicalTags, List<string> tags, TagName tagName)
        {
            if (technicalTags < _settings.MaxTechnicalTags)
            {
                ++technicalTags;
                tags.Add(String.Concat(_settings.TechnicalTagPrefix, tagName.Name));
            }

            return technicalTags;
        }

        private int AddSexualTagIfNotMax(int sexualTags, List<string> tags, TagName tagName)
        {
            if (sexualTags < _settings.MaxSexualTags)
            {
                ++sexualTags;
                tags.Add(String.Concat(_settings.SexualTagPrefix, tagName.Name));
            }

            return sexualTags;
        }

        private int AddContentTagIfNotMax(int contentTags, List<string> tags, TagName tagName)
        {
            if (contentTags < _settings.MaxContentTags)
            {
                ++contentTags;
                tags.Add(String.Concat(_settings.ContentTagPrefix, tagName.Name));
            }

            return contentTags;
        }

        private bool TagIsInEnabledCategory(TagName tagInfo)
        {
            return tagInfo.Cat.Equals("cont") && _settings.MaxContentTags > 0 ||
                   tagInfo.Cat.Equals("ero") && _settings.MaxSexualTags > 0 ||
                   tagInfo.Cat.Equals("tech") && _settings.MaxTechnicalTags > 0;
        }

        private (TagMetadata, TagName) MapTagToNamedTuple(TagMetadata tag)
        {
            var details = _tagDetails.Find(tagDetails =>
            {
                if (tagDetails.Id.Equals(tag.Id)) return true;

                return false;
            });
            return (tag, details);
        }

        private bool LowerSpoilerLevel(TagMetadata tag)
        {
            switch (tag.SpoilerLevel)
            {
                case VndbSharp.Models.Common.SpoilerLevel.None:
                    return true;
                case VndbSharp.Models.Common.SpoilerLevel.Minor:
                    return !_settings.TagMaxSpoilerLevel.Equals(SpoilerLevel.None);
                case VndbSharp.Models.Common.SpoilerLevel.Major:
                    return _settings.TagMaxSpoilerLevel.Equals(SpoilerLevel.Major);
                default:
                    return false;
            }
        }

        public override string GetDescription()
        {
            if (AvailableFields.Contains(MetadataField.Description) && _vnData != null)
                return _descriptionFormatter.Format(_vnData.Description);

            return base.GetDescription();
        }

        public override int? GetCommunityScore()
        {
            if (AvailableFields.Contains(MetadataField.CommunityScore) && _vnData != null) 
                return (int) (_vnData.Rating * 10.0);

            return base.GetCommunityScore();
        }

        public override MetadataFile GetCoverImage()
        {
            if (AvailableFields.Contains(MetadataField.CoverImage) && _vnData != null)
                if (IsImageAllowed(_vnData))
                    return new MetadataFile(_vnData.Image);


            return base.GetCoverImage();
        }

        public override MetadataFile GetBackgroundImage()
        {
            if (AvailableFields.Contains(MetadataField.BackgroundImage) && _vnData != null)
            {
                var selection = (from screenshot in _vnData.Screenshots
                    where IsImageAllowed(screenshot)
                    select new ImageFileOption(screenshot.Url)).ToList();

                var background = _playniteApi.Dialogs.ChooseImageFile(selection, "Screenshots");
                if (background != null) return new MetadataFile(background.Path);
            }

            return base.GetBackgroundImage();
        }

        public override List<Link> GetLinks()
        {
            if (AvailableFields.Contains(MetadataField.Links) && _vnData != null)
            {
                var links = new List<Link> {new Link("VNDB", "https://vndb.org/v" + _vnData.Id)};
                if (!string.IsNullOrWhiteSpace(_vnData.VisualNovelLinks.Renai))
                    links.Add(new Link("Renai", "https://renai.us/game/" + _vnData.VisualNovelLinks.Renai));
                if (!string.IsNullOrWhiteSpace(_vnData.VisualNovelLinks.Wikidata))
                    links.Add(new Link("Wikidata", "https://www.wikidata.org/wiki/" + _vnData.VisualNovelLinks.Wikidata));

                return links; 
            }
            
            return base.GetLinks();
        }
    }
}