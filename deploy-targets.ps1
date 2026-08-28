# Deployment targets shared by deploy-to-droplet.ps1 and deploy-secrets.ps1. Dot-source this file:
#   . (Join-Path $PSScriptRoot "deploy-targets.ps1")
# and pick an entry with Resolve-DeployTarget.
#
# RemoteEnvFile is the env file docker compose reads ON the droplet (--env-file); it must define
# YAAT_DOMAIN, VATSIM_CLIENT_ID, JWT_SIGNING_KEY, etc. (see yaat-server/.env.example). ".env" is the
# compose default, so a single-deployment droplet can keep using ".env".
$script:DeployTargets = @{
  yaat1 = @{
    DropletIp     = "143.198.111.198"
    ServerPath    = "/home/yaat/yaat-server"
    ServerUrl     = "https://yaat1.leftos.dev"
    RemoteEnvFile = ".env.yaat1"
  }
  # yaat2 = @{
  #   DropletIp     = "<droplet-ip>"
  #   ServerPath    = "/home/yaat/yaat-server"
  #   ServerUrl     = "https://yaat2.leftos.dev"
  #   RemoteEnvFile = ".env.yaat2"
  # }
}

function Resolve-DeployTarget {
  param([Parameter(Mandatory)][string]$Target)
  if (-not $script:DeployTargets.ContainsKey($Target)) {
    throw "Unknown target '$Target'. Known targets: $($script:DeployTargets.Keys -join ', '). Add it to deploy-targets.ps1."
  }
  return $script:DeployTargets[$Target]
}
