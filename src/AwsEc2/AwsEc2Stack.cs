using Amazon.CDK;
using Amazon.CDK.AWS.AutoScaling;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace AwsEc2;

public class AwsEc2Stack : Stack
{
    internal AwsEc2Stack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
    {
        // S3 bucket for CodeDeploy artifacts
        var deployBucket = new Bucket(this, "CodeDeployArtifactsBucket", new BucketProps {
            BucketName = "my-code-deploy-artifacts-bucket",
            Versioned = true,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true
        });
        
        // Lookup the default VPC
        var vpc = Vpc.FromLookup(this, "DefaultVpc", new VpcLookupOptions
        {
            IsDefault = true
        });

        // Create a security group
        var securityGroup = new SecurityGroup(this, "WebApiSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            AllowAllOutbound = true,
            Description = "Allow SSH and HTTP",
            SecurityGroupName = "WebApiSecurityGroup"
        });

        securityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(22), "Allow SSH access");
        securityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow HTTP access");
        securityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(5000), "MyApi App access");

        // Role for EC2
        var role = new Role(this, "Ec2Role", new RoleProps
        {
            AssumedBy = new ServicePrincipal("ec2.amazonaws.com")
        });

        role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("AmazonSSMManagedInstanceCore"));
        role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("AmazonEC2ReadOnlyAccess"));
        role.AddManagedPolicy(ManagedPolicy.FromAwsManagedPolicyName("CloudWatchAgentServerPolicy"));
        deployBucket.GrantRead(role);

        // Create Launch Template
        var machineImage = MachineImage.LatestAmazonLinux2023();
        var keyPair = KeyPair.FromKeyPairName(this, "key-0a498c763ef8ab954", "my-key-pair");
        
        // UserData script to install and configure CloudWatch Agent
        // CloudWatch Agent configuration as a readable JSON string
        var cloudWatchConfig = """
        {
            "agent": {
                "metrics_collection_interval": 60
            },
            "metrics": {
                "namespace": "CWAgent",
                "append_dimensions": {
                    "InstanceId": "${aws:InstanceId}"
                },
                "metrics_collected": {
                    "mem": {
                        "measurement": [
                            "used_percent",
                            "available_percent"
                        ]
                    },
                    "cpu": {
                        "measurement": [
                            "usage_active"
                        ]
                    },
                    "disk": {
                        "measurement": [
                            "used_percent",
                            "free"
                        ]
                    },
                    "processes": {
                        "measurement": [
                            "running",
                            "total_threads"
                        ]
                    }
                }
            }
        }
        """;
        
        var userData = UserData.ForLinux();
        userData.AddCommands(
            "#!/bin/bash",
            "set -xe",
            "yum update -y",
            "yum install -y amazon-cloudwatch-agent",
            
            // Ensure the directory exists
            "mkdir -p /opt/aws/amazon-cloudwatch-agent/bin/"
        );
        
        // Create CloudWatch Agent configuration file using heredoc
        userData.AddCommands("cat << 'EOF' > /opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent.json");
        userData.AddCommands(cloudWatchConfig);
        userData.AddCommands("EOF");
        
        userData.AddCommands(
            // Verify the file was created
            "ls -la /opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent.json",
            "cat /opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent.json",
            
            // Start CloudWatch Agent
            "/opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent-ctl -a fetch-config -m ec2 -s -c file:/opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent.json",
            
            // Enable CloudWatch Agent to start on boot
            "systemctl enable amazon-cloudwatch-agent",
            "systemctl start amazon-cloudwatch-agent"
        );
        
        var launchTemplate = new LaunchTemplate(this, "WebApiLaunchTemplate", new LaunchTemplateProps
        {
            InstanceType = InstanceType.Of(InstanceClass.BURSTABLE3, InstanceSize.MICRO),
            MachineImage = machineImage,
            SecurityGroup = securityGroup,
            KeyPair = keyPair,
            Role = role,
            UserData = userData
        });

        var lb = new ApplicationLoadBalancer(this, "MyALB", new ApplicationLoadBalancerProps {
            Vpc = vpc,
            InternetFacing = true,
            SecurityGroup = securityGroup
        });

        var listener = lb.AddListener("Listener", new BaseApplicationListenerProps {
            Port = 80,
            Open = true
        });
        
        var targetGroup = new ApplicationTargetGroup(this, "WebApiTargetGroup", new ApplicationTargetGroupProps
        {
            Vpc = vpc,
            Port = 5000,
            Protocol = ApplicationProtocol.HTTP,
            TargetType = TargetType.INSTANCE,
            HealthCheck = new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
            {
                Path = "/weatherforecast",
                Port = "5000"
            }
        });

        // Create Auto Scaling Group
        var autoScalingGroup = new AutoScalingGroup(this, "WebApiAutoScalingGroup", new AutoScalingGroupProps
        {
            Vpc = vpc,
            LaunchTemplate = launchTemplate,
            MinCapacity = 1,
            MaxCapacity = 4,
            DesiredCapacity = 2
        });

        // Attach Auto Scaling Group to Target Group
        autoScalingGroup.AttachToApplicationTargetGroup(targetGroup);

// Attach target group to listener
        listener.AddTargetGroups("AddWebApiTargetGroup", new AddApplicationTargetGroupsProps
        {
            TargetGroups = new IApplicationTargetGroup[] { targetGroup }
        });

        new CfnOutput(this, "LoadBalancerDNS", new CfnOutputProps
        {
            Value = lb.LoadBalancerDnsName,
            Description = "DNS of the load balancer"
        });

        new CfnOutput(this, "CodeDeployArtifactsBucketName", new CfnOutputProps {
            Value = deployBucket.BucketName,
            Description = "S3 bucket for CodeDeploy artifacts"
        });
    }
}