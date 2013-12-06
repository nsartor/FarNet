
<#
.Synopsis
	TabExpansion2 profile.
	Author: Roman Kuzmin

.Description
	This script should be in the path. It is invoked on the first call of the
	custom TabExpansion2. It adds code completers to the global option table.
	https://farnet.googlecode.com/svn/trunk/PowerShellFar/TabExpansion2.ps1

	The script reflects preferences of the author. Use it as the base for your
	own profile(s). Multiple profiles *TabExpansionProfile*.ps1 are supported.
#>

### FarHost completers
if ($Host.Name -ceq 'FarHost') {
	$TabExpansionOptions.CustomArgumentCompleters += @{
		### Find-FarFile - names from the active panel
		'Find-FarFile:Name' = {
			$Far.Panel.ShownFiles
		}
		### Out-FarPanel - properties + column info template
		'Out-FarPanel:Columns' = {
			param($commandName, $parameterName, $wordToComplete, $commandAst, $boundParameters)

			# properties
			if (($ast = $commandAst.Parent) -is [System.Management.Automation.Language.PipelineAst] -and $ast.PipelineElements.Count -eq 2) {
				try {
					(Invoke-Expression $ast.PipelineElements[0] | Get-Member $wordToComplete* -MemberType Properties).Name | Sort-Object -Unique
				}
				catch {}
			}

			# column info template
			"@{e=''; n=''; k=''; w=0; a=''}"
		}
	}
}

### Add common argument completers
$TabExpansionOptions.CustomArgumentCompleters += @{
	### Parameter ComputerName for all cmdlets
	'ComputerName' = {
		# add this machine first
		$name = $env:COMPUTERNAME
		New-CompletionResult $name

		# add others from the list
		foreach($_ in $env:pc_master, $env:pc_slave) { if ($_ -and $_ -ne $name) { New-CompletionResult $_ } }
	}
}

### Add native application completers
$TabExpansionOptions.NativeArgumentCompleters += @{
	### Far Manager command line switches
	'Far' = {
		param($wordToComplete, $commandAst)

		# default
		if ($wordToComplete) {return}

		# suggest all command line switches
		'/a','/ag','/clearcache','/co','/e','/export','/import','/m','/ma','/p','/ro','/rw','/s','/t','/u','/v','/w','/w-'
	}
}

### Add result processors
$TabExpansionOptions.ResultProcessors += {
	### WORD=[Tab] completions from TabExpansion.txt
	param($result, $ast, $tokens, $positionOfCursor, $options)

	# default
	if ($result.CompletionMatches) {return}

	# WORD=?
	if ("$ast".Substring($result.ReplacementIndex, $result.ReplacementLength) -notmatch '(^.*)=$') {return}
	$body = [regex]::Escape($matches[1])
	$head = "^$body"

	# completions from TabExpansion.txt in the TabExpansion2 script directory
	$path = [System.IO.Path]::GetDirectoryName((Get-Item Function:TabExpansion2).ScriptBlock.File)
	$lines = @(Get-Content -LiteralPath $path\TabExpansion.txt)
	$lines -match $body | Sort-Object {$_ -notmatch $head}, {$_} | .{process{
		if ($Host.Name -cne 'FarHost') {$_ = $_.Replace('#', '')}
		$result.CompletionMatches.Add((New-CompletionResult $_ -ResultType Text))
	}}
},{
	### WORD#[Tab] completions from history
	param($result, $ast, $tokens, $positionOfCursor, $options)

	# default
	if ($result.CompletionMatches) {return}

	# WORD#?
	if ("$ast".Substring($result.ReplacementIndex, $result.ReplacementLength) -notmatch '(^.*)#$') {return}
	$body = [regex]::Escape($matches[1])

	$_ = [System.Collections.ArrayList](@(Get-History -Count 9999) -match $body)
	$_.Reverse()
	$_ | .{process{ $result.CompletionMatches.Add((New-CompletionResult $_ -ResultType History)) }}
},{
	### Complete an alias as definition and remove itself
	param($result, $ast, $tokens, $positionOfCursor, $options)

	$token = foreach($_ in $tokens) {if ($_.Extent.EndOffset -eq $positionOfCursor.Offset) {$_; break}}
	if (!$token -or $token.TokenFlags -ne 'CommandName') {return}

	# aliases
	$name = "$token"
	$aliases = @(Get-Alias $name -ErrorAction Ignore)
	if ($aliases.Count -ne 1) {return}

	# remove itself
	for($i = $result.CompletionMatches.Count; --$i -ge 0) {
		if ($result.CompletionMatches[$i].CompletionText -eq $name) {
			$result.CompletionMatches.RemoveAt($i)
			break
		}
	}

	# insert first
	$result.CompletionMatches.Insert(0, (New-CompletionResult $aliases[0].Definition -ResultType Command))
},{
	### Complete help comments like .Synopsis, .Description.
	param($result, $ast, $tokens, $positionOfCursor, $options)

	# match the whole text
	$line = $positionOfCursor.Line.TrimEnd()
	if ($line -notmatch '^\s*(#*\s*)(\.\w*)$' -or $positionOfCursor.Offset -ne $line.Length) {return}

	# insert help tags
	$i = 0
	@(
		'.Synopsis'
		'.Description'
		'.Parameter'
		'.Inputs'
		'.Outputs'
		'.Notes'
		'.Example'
		'.Link'
		'.Component'
		'.Role'
		'.Functionality'
		'.ForwardHelpTargetName'
		'.ForwardHelpCategory'
		'.RemoteHelpRunspace'
		'.ExternalHelp'
	) -like "$($matches[2])*" | .{process{
		$result.CompletionMatches.Insert($i++, (New-CompletionResult ($matches[1] + $_) -ResultType ParameterName))
	}}
},{
	### Complete variable $*var
	param($result, $ast, $tokens, $positionOfCursor, $options)

	$token = foreach($_ in $tokens) {if ($_.Extent.EndOffset -eq $positionOfCursor.Offset) {$_; break}}
	if (!$token -or $token -notmatch '^\$(\*.*)') {return}

	foreach($_ in Get-Variable "$($matches[1])*") {
		$result.CompletionMatches.Add((New-CompletionResult "`$$($_.Name)" -ResultType Variable))
	}
}

### Add input processors
$TabExpansionOptions.InputProcessors += {
	### Complete [Type/Namespace[Tab]
	# Expands one piece at a time, e.g. [System. | [System.Data. | [System.Data.CommandType]
	# If pattern in "[pattern" contains wildcard characters all types are searched for the match.
	param($ast, $tokens, $positionOfCursor, $options)

	$token = foreach($_ in $tokens) {if ($_.Extent.EndOffset -eq $positionOfCursor.Offset) {$_; break}}
	if (!$token -or ($token.TokenFlags -ne 'TypeName' -and $token.TokenFlags -ne 'CommandName')) {return}

	$line = $positionOfCursor.Line.Substring(0, $positionOfCursor.Offset)
	if ($line -notmatch '\[([\w.*?]+)$') {return}

	# fake
	function TabExpansion($line, $lastWord) { GetTabExpansionType $matches[1] '[' }
	$result = [System.Management.Automation.CommandCompletion]::CompleteInput($line, $positionOfCursor.Offset, $null)

	# ISE
	if ($Host.Name -eq 'Windows PowerShell ISE Host') {
		for($i = $result.CompletionMatches.Count; --$i -ge 0) {
			$text = $result.CompletionMatches[$i].CompletionText
			if ($text -match '\.([^.]+(\.)?)$') {
				$type = if ($matches[2] -ceq '.') {'Namespace'} else {'Type'}
				$result.CompletionMatches[$i] = New-CompletionResult $text "[$($matches[1])" $type
			}
		}
	}

	$result
}

<#
.Synopsis
	Gets namespace and type names for TabExpansion.
.Parameter pattern
		Pattern to search for matches.
.Parameter prefix
		Prefix used by TabExpansion.
#>
function global:GetTabExpansionType
(
	$pattern,
	[string]$prefix
)
{
	$suffix = if ($prefix) {']'} else {''}

	# wildcard type
	if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($pattern)) {
		.{ foreach($assembly in [System.AppDomain]::CurrentDomain.GetAssemblies()) {
			try {
				foreach($_ in $assembly.GetExportedTypes()) {
					if ($_.FullName -like $pattern) {
						"$prefix$($_.FullName)$suffix"
					}
				}
			}
			catch { $Error.RemoveAt(0) }
		}} | Sort-Object
		return
	}

	# patterns
	$escaped = [regex]::Escape($pattern)
	$re1 = [regex]"(?i)^($escaped[^.]*)"
	$re2 = [regex]"(?i)^($escaped[^.``]*)(?:``(\d+))?$"
	if (!$pattern.StartsWith('System.', 'OrdinalIgnoreCase')) {
		$re1 = $re1, [regex]"(?i)^System\.($escaped[^.]*)"
		$re2 = $re2, [regex]"(?i)^System\.($escaped[^.``]*)(?:``(\d+))?$"
	}

	# namespaces and types
	$1 = @{}
	$2 = [System.Collections.ArrayList]@()
	foreach($assembly in [System.AppDomain]::CurrentDomain.GetAssemblies()) {
		try { $types = $assembly.GetExportedTypes() }
		catch { $Error.RemoveAt(0); continue }
		$n = [System.Collections.Generic.HashSet[object]]@(foreach($_ in $types) {$_.Namespace})
		foreach($r in $re1) {
			foreach($_ in $n) {
				if ($_ -match $r) {
					$1["$prefix$($matches[1])."] = $null
				}
			}
		}
		foreach($r in $re2) {
			foreach($_ in $types) {
				if ($_.FullName -match $r) {
					if ($matches[2]) {
						$null = $2.Add("$prefix$($matches[1])[$(''.PadRight(([int]$matches[2] - 1), ','))]$suffix")
					}
					else {
						$null = $2.Add("$prefix$($matches[1])$suffix")
					}
				}
			}
		}
	}
	$1.Keys | Sort-Object
	$2 | Sort-Object
}
