#pragma warning disable CS1591, CS1573, SA1611, SA1618, SA1402, SA1300, CA1819, SA1005, SA1629, SA1201, CA1860, CA1869

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SemPick;
using SemPick.Sorters;

namespace Jellyfin.Api.Controllers
{
  /// <summary>
  /// Controller used for 10 foot interface searching.
  /// </summary>
  [Route("Sempick")]
  [Authorize]
  public class SempickController : BaseJellyfinApiController
  {
    private static readonly object _orchestrationLock = new();
    private static EngineOrchestration? _orchestration;

    private readonly ILogger<SempickController> _logger;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SempickController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SempickController}"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    public SempickController(
        ILogger<SempickController> logger,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ISessionManager sessionManager)
    {
      _logger = logger;
      _userManager = userManager;
      _libraryManager = libraryManager;
      _dtoService = dtoService;
      _sessionManager = sessionManager;
    }

    /// <summary>
    /// Authenticates a user by username and password, returning an access token for use with all other Sempick endpoints.
    /// </summary>
    /// <param name="username">The Jellyfin username.</param>
    /// <param name="password">The plain-text password.</param>
    /// <returns>An <see cref="AuthenticationResult"/> containing the access token.</returns>
    [HttpPost("Login")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthenticationResult>> Login([FromQuery] string username, [FromQuery] string password)
    {
      var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
      _logger.LogInformation("Sempick login attempt for user '{Username}' from {RemoteIp}", username, remoteIp);
      try
      {
        var result = await _sessionManager.AuthenticateNewSession(new AuthenticationRequest
        {
          Username = username,
          Password = password,
          App = "Sempick",
          AppVersion = "1.0",
          DeviceId = remoteIp,
          DeviceName = "Sempick Client",
          RemoteEndPoint = remoteIp
        }).ConfigureAwait(false);

        _logger.LogInformation("Sempick login succeeded for user '{Username}'", username);
        return result;
      }
      catch (AuthenticationException ex)
      {
        _logger.LogWarning("Sempick login failed for user '{Username}' from {RemoteIp}: {Message}", username, remoteIp, ex.Message);
        return Unauthorized();
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Unexpected error during Sempick login for user '{Username}'", username);
        return StatusCode(StatusCodes.Status500InternalServerError);
      }
    }

    /// <summary>
    /// Returns the current SemPick selection state for the given input sequence.
    /// The full selection history must be submitted on every call; the server resets and replays from scratch.
    /// The underlying fragment index is cached and shared across requests. Call <c>POST /Sempick/InvalidateCache</c>
    /// after a library scan to rebuild it.
    /// </summary>
    /// <param name="userId">The user whose library is searched.</param>
    /// <param name="limit">Optional. Maximum number of library items to include.</param>
    /// <param name="submittedSemSequences">Optional. JSON array of digit strings representing previous key presses, e.g. <c>["123","4"]</c>.</param>
    /// <returns>A JSON object describing the current engine result.</returns>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<string> GetItems(
        [FromQuery] Guid? userId,
        [FromQuery] int? limit,
        [FromQuery] string? submittedSemSequences)
    {
      var user = GetUser(userId);

      string[] selections;
      try
      {
        selections = submittedSemSequences is null
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(submittedSemSequences) ?? Array.Empty<string>();
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Sempick received malformed submittedSemSequences: {Value}", submittedSemSequences);
        return BadRequest("Invalid submittedSemSequences: expected a JSON array of digit strings.");
      }

      if (selections.Any(s => s.Any(c => c < '0' || c > '9')))
      {
        _logger.LogWarning("Sempick received non-digit characters in submittedSemSequences: {Value}", submittedSemSequences);
        return BadRequest("submittedSemSequences must contain only digit characters ('0'–'9').");
      }

      EngineResult results;
      lock (_orchestrationLock)
      {
        if (_orchestration is null)
        {
          _logger.LogInformation("Sempick building fragment index (limit={Limit})", limit);
          var comparer = new KeyboardUtilityComparer<SemPickFragment>(x => x.fragment);
          var query = new InternalItemsQuery(user) { Recursive = true, Limit = limit };
          var items = _libraryManager.GetUserRootFolder().GetItemList(query);
          var fragments = InitializeFragmentsWJelly(items, comparer, user);
          _logger.LogInformation("Sempick fragment index built: {Count} fragments", fragments.Length);
          var engine = EngineBuilders.Create_AllWordViaKeyboardAutoSelect(fragments, 4);
          _orchestration = new EngineOrchestration(engine, 16);
        }

        _orchestration.Reset();
        foreach (var ss in selections)
        {
          foreach (var c in ss)
          {
            _orchestration.Pick((KeyInput)(c - '0'));
          }
        }

        results = _orchestration.LastResult;
      }

      _logger.LogDebug(
          "Sempick GetItems: selections={Selections} remaining={Remaining} state={State}",
          submittedSemSequences,
          results.Remaining,
          results.state);

      var json = EngineResultWJellyDtoToJson(results, item => ItemConverter(item, _dtoService, user));
      return new ContentResult { Content = json, ContentType = "application/json" };

      static BaseItemDto ItemConverter(BaseItem item, IDtoService dtoService, User? user)
      {
        return dtoService.GetBaseItemDto(item, new DtoOptions(true));
      }
    }

    /// <summary>
    /// Clears the cached fragment index so it will be rebuilt on the next request.
    /// Call this after a Jellyfin library scan completes.
    /// </summary>
    /// <returns>204 No Content.</returns>
    [HttpPost("InvalidateCache")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult InvalidateCache()
    {
      lock (_orchestrationLock)
      {
        _orchestration = null;
      }

      _logger.LogInformation("Sempick fragment cache invalidated");
      return NoContent();
    }

    private SemPickFragment[] InitializeFragmentsWJelly(IEnumerable<BaseItem> items, IComparer<SemPickFragment> comparer, User? user)
    {
      var fragments = items
        .Where(x => x.Name is not null)
        .Select((x, i) => new SemPickFragmentWJelly(x.Name.Clean(StringCleaning.CleanCharAllowSpace), 1, i, new JellyFragment(x.Id, x.Name, x), null))
        .GroupBy(x => x.fragment, (key, dups) => dups.First() with { Jellies = dups.Select(x => x.Jelly).ToArray() })
        .Order(comparer)
        .ToArray();
      return fragments;
    }

    private string EngineResultWJellyDtoToJson(EngineResult engineResult, Func<BaseItem, BaseItemDto> baseItemConverter)
    {
      var serializeOptions = new JsonSerializerOptions();
      serializeOptions.Converters.Add(new SemPickFragmentWJellyJsonConverter2(baseItemConverter));
      serializeOptions.Converters.Add(new JsonStringEnumConverter());
      return JsonSerializer.Serialize(engineResult, serializeOptions);
    }

    private User? GetUser(Guid? userId, string? username = null)
    {
      if (!string.IsNullOrEmpty(username) && (userId is null || userId.Value.Equals(default)))
      {
        return _userManager.GetUserByName(username) ?? throw new ResourceNotFoundException();
      }

      var isApiKey = User.GetIsApiKey();
      userId = RequestHelpers.GetUserId(User, userId);
      var user = !isApiKey && !userId.Value.Equals(default)
          ? _userManager.GetUserById(userId.Value) ?? throw new ResourceNotFoundException()
          : null;
      return user;
    }
  }

  public record SemPickFragmentWJelly(string fragment, int Count, int Index, JellyFragment Jelly, JellyFragment[]? Jellies) : SemPickFragment(fragment, Count, Index);

  public record JellyFragment(Guid Id, string Name, BaseItem Item);

  public record JellyFragmentDto(Guid Id, string Name, BaseItemDto Item);

  public class SemPickFragmentWJellyJsonConverter2 : JsonConverter<SemPickFragment>
  {
    private readonly Func<BaseItem, BaseItemDto> _baseItemConverter;

    public SemPickFragmentWJellyJsonConverter2(Func<BaseItem, BaseItemDto> baseItemConverter)
    {
      _baseItemConverter = baseItemConverter;
    }

    public override void Write(Utf8JsonWriter writer, SemPickFragment value, JsonSerializerOptions options)
    {
      writer.WriteStartObject();
      writer.WriteString("fragment", value.fragment);
      writer.WriteNumber("Count", value.Count);
      writer.WriteNumber("Index", value.Index);
      if (value is SemPickFragmentWJelly jellyFragment && jellyFragment.Jelly is not null)
      {
        var jelly = jellyFragment.Jelly;
        var dto = new JellyFragmentDto(jelly.Id, jelly.Name, _baseItemConverter(jelly.Item));
        writer.WritePropertyName("Jelly");
        writer.WriteRawValue(JsonSerializer.Serialize(dto));
        if (jellyFragment.Jellies?.Any() ?? false)
        {
          var itemDtos = jellyFragment.Jellies.Select(x => new JellyFragmentDto(x.Id, x.Name, _baseItemConverter(x.Item)));
          writer.WritePropertyName("Jellies");
          writer.WriteRawValue(JsonSerializer.Serialize(itemDtos));
        }
      }

      writer.WriteEndObject();
    }

    public override SemPickFragment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
      throw new NotImplementedException();
    }
  }
}
