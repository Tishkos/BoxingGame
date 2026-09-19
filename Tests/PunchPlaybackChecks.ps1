$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../Assets/UndisputedTest/Boxing/PunchPlayback.cs')
$script:checks = 0

function Check([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
    $script:checks++
}

$timing = [PunchPlayback]::CreateTiming(0.73, 0.55, 0.81, 0.84)
Check ($timing.ContactStart -lt $timing.Strike) 'Contact must open before impact.'
Check ($timing.ContactEnd -gt $timing.Strike) 'Contact must close after impact.'
Check ($timing.End -gt $timing.ContactEnd) 'Recovery must retain follow-through.'
foreach ($alternate in @($false, $true)) {
    $gate = [PunchPlayback]::ComboTime($timing, 0.6, $alternate)
    Check ($gate -gt $timing.ContactEnd) 'A late-impact clip cannot be cancelled before contact closes.'
    Check ($gate -le $timing.End) 'The combo gate must be reachable.'
}

$timing = [PunchPlayback]::CreateTiming(0.4, -1, -1, -1)
$same = [PunchPlayback]::ComboTime($timing, 0.7, $false)
$alternate = [PunchPlayback]::ComboTime($timing, 0.7, $true)
Check ($alternate -lt $same) 'Alternating hands should chain earlier than repeating one hand.'

foreach ($strike in @(0.15, 0.4, 0.73)) {
    $speed = [PunchPlayback]::Speed(1.2, $strike, 0.3, 1, 1)
    Check ([Math]::Abs((1.2 * $strike / $speed) - 0.3) -lt 0.0001) 'Matched punches must reach their authored impact on time.'
    $tired = [PunchPlayback]::Speed(1.2, $strike, 0.3, 1, 0.6)
    Check ($tired -lt $speed -and $tired -gt 0) 'Fatigue must slow the punch without stalling it.'
}

foreach ($value in @([float]::NaN, [float]::PositiveInfinity, -10, 10)) {
    $t = [PunchPlayback]::CreateTiming($value, $value, $value, $value)
    Check ($t.ContactStart -ge 0 -and $t.ContactStart -lt $t.Strike) 'Invalid markers must produce a valid contact start.'
    Check ($t.ContactEnd -gt $t.Strike -and $t.End -gt $t.ContactEnd -and $t.End -le 1) 'Invalid markers must produce finite ordered phases.'
    $speed = [PunchPlayback]::Speed($value, $value, $value, $value, $value)
    Check ($speed -gt 0 -and $speed -le 6) 'Bad speed tuning must never produce NaN, infinity, or a stall.'
}

Check ([PunchPlayback]::AimEnvelope(0, 0.4, 0.2, 0.05) -eq 0) 'IK must not steer the initial guard.'
Check ([PunchPlayback]::AimEnvelope(0.4, 0.4, 0.2, 0.05) -eq 1) 'IK must peak at impact.'
Check ([PunchPlayback]::AimEnvelope(1, 0.4, 0.2, 0.05) -eq 0) 'IK must release the recovered hand.'
foreach ($strike in @(0.08, 0.15, 0.73, 0.9)) {
    Check ([PunchPlayback]::AimEnvelope(0, $strike, 0.3, 0.1) -eq 0) 'Even an early-impact clip must start with zero IK.'
    Check ([PunchPlayback]::AimEnvelope(1, $strike, 0.3, 0.1) -eq 0) 'Even a late-impact clip must finish with zero IK.'
}
foreach ($fps in @(30, 60, 144)) {
    for ($frame = 0; $frame -le $fps; $frame++) {
        $value = [PunchPlayback]::AimEnvelope(($frame / [float]$fps), 0.4, 0.2, 0.05)
        Check ($value -ge 0 -and $value -le 1) 'IK envelope must remain bounded at every frame rate.'
    }
}

$buffer = New-Object 'PunchBuffer[int]' 2
Check ($buffer.Enqueue(10, 1, 0.3)) 'First press must queue.'
Check ($buffer.Enqueue(20, 1.05, 0.3)) 'Second press must queue.'
Check (-not $buffer.Enqueue(30, 1.1, 0.3)) 'Mashing must not create an unbounded automatic combo.'
$value = 0
Check ($buffer.TryPeek(1.15, [ref]$value) -and $value -eq 10) 'The buffer must preserve the first requested punch.'
$buffer.Dequeue()
Check ($buffer.TryPeek(1.2, [ref]$value) -and $value -eq 20) 'The buffer must preserve input order.'
Check (-not $buffer.TryPeek(1.36, [ref]$value)) 'Old input must expire instead of firing unexpectedly.'
Check ($buffer.Enqueue(40, 2, 0.3)) 'A drained buffer must accept new input.'
$buffer.Clear()
Check (-not $buffer.TryPeek(2, [ref]$value)) 'Focus loss or disable must clear buffered input.'

Check (-not $buffer.Enqueue(50, 3, [float]::PositiveInfinity)) 'An infinite lifetime must not create a permanently buffered punch.'
Check (-not $buffer.Enqueue(50, [float]::NaN, 0.3)) 'Invalid timestamps must be rejected.'
Check (-not $buffer.Enqueue(50, 3, 0)) 'Zero-duration requests must be rejected.'

Check ([PunchPlayback]::StickTechnique(0, 0.55) -eq 0) 'Centred stick must select a straight.'
Check ([PunchPlayback]::StickTechnique(1, 0.55) -eq 1) 'Stick up must select a hook for either trigger.'
Check ([PunchPlayback]::StickTechnique(-1, 0.55) -eq 2) 'Stick down must select an uppercut while punching.'
Check ([PunchPlayback]::StickTechnique([float]::NaN, 0.55) -eq 0) 'Invalid stick input must not select an attack.'
Check ([PunchPlayback]::GuardFromShoulders($true, $false) -eq 1) 'L1 must select body defense, never a hook.'
Check ([PunchPlayback]::GuardFromShoulders($false, $true) -eq 2) 'R1 must select head defense, never a hook.'
Check ([PunchPlayback]::GuardFromShoulders($true, $true) -eq 2) 'Both shoulders must have a stable head-guard priority.'
Check ([PunchPlayback]::GuardFromShoulders($false, $false) -eq 0) 'Released shoulders must release defense.'
Check (-not [PunchPlayback]::CanRaiseGuard($true, 0.4, 0.6)) 'Guard must not cancel an extending punch.'
Check ([PunchPlayback]::CanRaiseGuard($true, 0.61, 0.6)) 'Guard should become available after follow-through.'
Check ([PunchPlayback]::CanRaiseGuard($false, 0, 0.6)) 'Guard must work from idle.'
Check ([PunchPlayback]::LeanAuthority($false, $false, $false) -eq 1) 'An idle right stick must lean.'
Check ([PunchPlayback]::LeanAuthority($true, $false, $false) -eq 0) 'A held punch trigger must not also lean.'
Check ([PunchPlayback]::LeanAuthority($true, $true, $false) -eq 1) 'Guarding must allow leaning even if a trigger is held.'
Check ([PunchPlayback]::LeanAuthority($false, $true, $true) -eq 0) 'A punch in flight must not receive a sudden defensive lean.'
Check ([PunchPlayback]::DeadzoneMagnitude(0.1, 0.15) -eq 0) 'Small stick drift must not lean the fighter.'
Check ([PunchPlayback]::DeadzoneMagnitude(1, 0.15) -eq 1) 'Full stick must reach the configured lean.'
Check ([PunchPlayback]::DeadzoneMagnitude(2, 0.15) -eq 1) 'Diagonal lean must remain bounded.'
foreach ($strike in @(0.15, 0.4, 0.73)) {
    Check ([PunchPlayback]::SteeringAuthority(0, $strike) -eq 1) 'A punch must allow early steering.'
    Check ([PunchPlayback]::SteeringAuthority(($strike * 0.65), $strike) -gt 0) 'Steering must taper smoothly, not switch off at launch.'
    Check ([PunchPlayback]::SteeringAuthority(($strike * 0.9), $strike) -eq 0) 'Steering must lock before contact.'
    Check ([PunchPlayback]::SteeringAuthority($strike, $strike) -eq 0) 'Steering must not redirect a landed punch.'
}

Check ([PunchPlayback]::CanSteer($true, $true, $true)) 'A held preparation must allow bounded steering.'
Check (-not [PunchPlayback]::CanSteer($true, $false, $true)) 'Re-pressing a trigger must not steer an already released punch.'
Check (-not [PunchPlayback]::CanSteer($true, $true, $false)) 'Releasing the trigger must lock preparation steering.'
Check ([PunchPlayback]::CanSteer($false, $false, $true)) 'Press-to-punch mode must retain early held-trigger steering.'
Check (-not [PunchPlayback]::CanSteer($false, $false, $false)) 'Press-to-punch mode must not steer without a held trigger.'

Check ([PunchPlayback]::ChargeAmount(0.08, 0.1, 0.7) -eq 0) 'A quick tap must not charge.'
Check ([PunchPlayback]::ChargeAmount(0.4, 0.1, 0.7) -gt 0) 'A held punch must build charge.'
Check ([PunchPlayback]::ChargeAmount(10, 0.1, 0.7) -eq 1) 'Charge must cap even when the trigger stays held.'
Check ([PunchPlayback]::ChargePower(1, 1, 0.25) -eq 1.25) 'Fresh full charge must give only the configured power bonus.'
Check ([PunchPlayback]::ChargePower(1, 0, 0.25) -eq 1) 'Exhaustion must remove the charge bonus.'
Check ([PunchPlayback]::ChargePower(0, 1, 0.25) -eq 1) 'Uncharged punches must not gain free power.'
foreach ($fps in @(30, 60, 144)) {
    $hold = New-Object PunchHold
    $hold.Begin()
    $spent = 0.0
    for ($frame = 0; $frame -lt ($fps * 2); $frame++) {
        $spent += $hold.Advance((1.0 / $fps), 0.1, 8)
    }
    Check ([Math]::Abs($spent - 15.2) -lt 0.002) 'Holding must drain stamina continuously and independently of frame rate.'
    Check ($hold.Active) 'Full charge must not automatically fire the punch.'
    $charge = $hold.Release(0.1, 0.7)
    Check ($charge -eq 1 -and -not $hold.Active) 'Release must consume the held charge once.'
    Check ($hold.Release(0.1, 0.7) -eq 0) 'Repeated release must not duplicate a charged punch.'
    $hold.Begin()
    $null = $hold.Advance(0.5, 0.1, 8)
    $hold.Cancel()
    Check ($hold.Release(0.1, 0.7) -eq 0) 'Guard cancellation or focus loss must discard charge.'
}
foreach ($strike in @(0.15, 0.4, 0.73)) {
    $t = [PunchPlayback]::CreateTiming($strike, -1, -1, -1)
    $chamber = [PunchPlayback]::ChamberTime($t)
    Check ($chamber -lt $t.Strike -and $chamber -le $t.ContactStart) 'Preparation must stay before the contact window.'
    Check ([PunchPlayback]::PreparationTime(0, 0.18, $t) -eq 0) 'Preparation must begin at the start of the authored motion.'
    Check ([Math]::Abs([PunchPlayback]::PreparationTime(10, 0.18, $t) - $chamber) -lt 0.0001) 'Long holds must settle on a safe chamber frame.'
    $speed = [PunchPlayback]::Speed(1.2, $strike, 0.3, 1, 1)
    $remaining = 1.2 * ($strike - $chamber) / $speed
    Check ([Math]::Abs($remaining - (0.3 * (1 - $chamber / $strike))) -lt 0.0001) 'Release must retain the matched clip speed rather than replaying its wind-up.'
}
$hold = New-Object PunchHold
$hold.Begin()
Check ($hold.Advance(0, 0.1, 8) -eq 0 -and $hold.HeldSeconds -eq 0) 'Pause must not charge or drain stamina.'
Check ($hold.Advance([float]::NaN, 0.1, 8) -eq 0 -and $hold.HeldSeconds -eq 0) 'Invalid time steps must not poison charge.'
Check ([PunchPlayback]::HoldCost(2, 1, 0.1, 8) -eq 8) 'Stamina drain must continue after full charge.'
Check ([PunchPlayback]::HoldCost(100000000, 0.016, 0.1, 8) -gt 0) 'Long-held input must not erase the per-frame stamina drain through float cancellation.'

$leftHold = New-Object PunchHold
$rightHold = New-Object PunchHold
$leftHold.Begin()
$rightHold.Begin()
Check ($leftHold.Advance(0.08, 0.1, 8) -eq 0) 'A quick tap must pay no extra holding cost.'
$rightCost = $rightHold.Advance(0.7, 0.1, 8)
Check ([Math]::Abs($rightCost - 4.8) -lt 0.0001) 'The other held hand must drain its own stamina interval.'
Check ($leftHold.Release(0.1, 0.7) -eq 0 -and -not $leftHold.Active) 'A quick release must consume an uncharged hold.'
Check ($rightHold.Active -and [Math]::Abs($rightHold.HeldSeconds - 0.7) -lt 0.0001) 'Releasing one hand must not clear the other held hand.'
Check ($rightHold.Release(0.1, 0.7) -eq 1) 'The other hand must retain its charge until released.'
$leftHold.Begin()
Check ($leftHold.HeldSeconds -eq 0) 'A new press must not inherit the previous hold duration.'
$leftHold.Cancel()
Check ($leftHold.Advance(1, 0.1, 8) -eq 0 -and -not $leftHold.Active) 'A cancelled hold must not keep spending stamina.'

Check ([Math]::Abs([PunchPlayback]::StaminaSpeed(0, 0.22) - 0.22) -lt 0.0001) 'Empty stamina must produce the strongly slowed configured speed.'
Check ([PunchPlayback]::StaminaSpeed(1, 0.22) -eq 1) 'Full stamina must retain full speed.'
Check ([PunchPlayback]::StaminaSpeed(0.1, 0.22) -lt 0.27) 'Very low stamina must remain visibly slow.'
$previous = 0.0
foreach ($energy in @(0, 0.05, 0.1, 0.25, 0.5, 0.75, 1)) {
    $fatigue = [PunchPlayback]::StaminaSpeed($energy, 0.22)
    Check ($fatigue -ge $previous -and $fatigue -le 1) 'Stamina speed must increase monotonically without overshoot.'
    $previous = $fatigue
}
Check ([PunchPlayback]::ComboSpeed(0.3, 3, 0.1) -eq 1) 'Combo acceleration must not erase exhausted slowdown.'
Check ([Math]::Abs([PunchPlayback]::ComboSpeed(0.07, 3, 1) - 1.14) -lt 0.0001) 'Optional fresh combo acceleration must remain bounded.'
Check ([PunchPlayback]::ComboSpeed(0, 3, 1) -eq 1) 'Matched timing must not make later hands faster by default.'
Check ([PunchPlayback]::StickTechnique(0, 0, 0.55) -eq 0) 'Triggers alone must select the dedicated jab.'
Check ([PunchPlayback]::StickTechnique(0.71, 0.71, 0.55) -eq 0) 'Diagonal lateral control must not accidentally select a hook.'
Check ([PunchPlayback]::StickTechnique(0.2, 0.9, 0.55) -eq 1) 'Intentional stick up must still select a hook.'
Check ([PunchPlayback]::StickTechnique(-0.2, -0.9, 0.55) -eq 2) 'Intentional stick down must still select an uppercut.'
foreach ($energy in @(0, 0.1, 0.5, 1)) {
    $fatigue = [PunchPlayback]::StaminaSpeed($energy, 0.22)
    foreach ($elapsed in @(0, 0.06, 0.18, 1)) {
        $budget = [PunchPlayback]::ReleaseImpactTime(0.3, $elapsed, 0.18)
        foreach ($clip in @(@(0.6484585, 0.3631973, 0.1711322), @(1.1815134, 0.4406448, 0.3038963), @(0.7, 0.08, 0))) {
            $t = [PunchPlayback]::CreateTiming($clip[1], $clip[2], -1, -1)
            $prepared = [PunchPlayback]::PreparationTime($elapsed, 0.18, $t)
            $speed = [PunchPlayback]::ReleaseSpeed($clip[0], $t.Strike, $prepared, $budget, 1, $fatigue)
            $arrival = $clip[0] * ($t.Strike - $prepared) / $speed
            Check ([Math]::Abs($arrival - $budget / $fatigue) -lt 0.0001) 'Different hands/clip shapes must match release-to-impact time at each stamina and preparation level.'
            Check ($prepared -le $t.ContactStart) 'Matching release speed must not move preparation into contact.'
        }
    }
}
Check ([PunchPlayback]::ReleaseSpeed(0.2, 0.1, 0, 0.3, 1, 0.22) -lt 0.15) 'A playback floor must not make short exhausted clips disproportionately fast.'
foreach ($bad in @([float]::NaN, [float]::PositiveInfinity, -10, 10)) {
    $speed = [PunchPlayback]::StaminaSpeed($bad, $bad)
    Check ($speed -ge 0.1 -and $speed -le 1) 'Invalid stamina tuning must produce finite bounded speed.'
}
foreach ($fps in @(30, 60, 144)) {
    $cadence = New-Object FootstepCadence
    $steps = 0
    for ($frame = 0; $frame -lt ($fps * 2); $frame++) {
        if ($cadence.Advance((1.5 / $fps), 0.65, $true)) { $steps++ }
    }
    Check ($steps -eq 4) 'Soft shuffle cadence must follow travelled distance consistently across frame rates.'
    Check (-not $cadence.Advance(1, 0.65, $false)) 'Idle or disabled movement must not emit footsteps.'
    Check (-not $cadence.Advance(0.4, 0.65, $true)) 'Stopping must clear leftover travel rather than immediately firing on restart.'
    $cadence.Reset()
    Check (-not $cadence.Advance([float]::NaN, 0.65, $true)) 'Invalid travel must not play or poison footsteps.'
    Check ($cadence.Advance(0.7, 0.65, $true)) 'A valid step must still work after invalid input.'
}

Check ([PunchPlayback]::GuardCoverage($true, 1, 0, 1) -eq 1) 'Head guard must cover a frontal head shot.'
Check ([PunchPlayback]::GuardCoverage($false, 1, 0, 1) -eq 0) 'Head guard must not also block body shots.'
Check ([PunchPlayback]::GuardCoverage($false, 0, 1, 1) -eq 1) 'Body guard must cover a frontal body shot.'
Check ([PunchPlayback]::GuardCoverage($true, 0, 1, 1) -eq 0) 'Body guard must leave the head exposed.'
Check ([PunchPlayback]::GuardCoverage($true, 1, 1, -1) -eq 0) 'A frontal guard must not block a rear hit.'
Check ([Math]::Abs([PunchPlayback]::GuardedPower(1, 1, 0.18) - 0.18) -lt 0.0001) 'A full guard must retain only its configured leakage.'
Check ([Math]::Abs([PunchPlayback]::GuardedPower(1, 0.5, 0.18) - 0.59) -lt 0.0001) 'Guard protection must blend with the actual pose weight.'
Check (-not [PunchPlayback]::CanDamageFighter(1,1,$true,$false,$false)) 'Study sensors must never hit their owner.'
Check (-not [PunchPlayback]::CanDamageFighter(0,2,$true,$false,$false)) 'An unowned sensor must not damage a fighter.'
Check (-not [PunchPlayback]::CanDamageFighter(1,2,$false,$false,$false)) 'An unarmed glove must not damage a fighter.'
Check (-not [PunchPlayback]::CanDamageFighter(1,2,$true,$true,$false)) 'A landed strike must not deal a second hit.'
Check (-not [PunchPlayback]::CanDamageFighter(1,2,$true,$false,$true)) 'A downed fighter must not take follow-up damage.'
Check ([PunchPlayback]::CanDamageFighter(1,2,$true,$false,$false)) 'An eligible opponent contact must be allowed.'
$reaction = New-Object FightReactionWindow
Check (-not $reaction.Active(0)) 'The AI must not begin with a free defensive reaction.'
$reaction.Schedule(10, 0.2, 0.45)
Check (-not $reaction.Active(10.19)) 'AI defense must wait for its reaction delay.'
Check ($reaction.Active(10.21)) 'AI defense must engage after its delay.'
Check (-not $reaction.Active(10.66)) 'AI defense must expire instead of guarding forever.'
$reaction.Clear()
Check (-not $reaction.Active(10.3)) 'Resetting a bout must clear pending AI reactions.'

Write-Output "PASS: $script:checks punch, charge, defense, steering, lean, input-buffer, and footstep checks."
